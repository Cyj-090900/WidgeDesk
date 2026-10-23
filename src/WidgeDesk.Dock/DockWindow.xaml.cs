using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Contracts;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform;
using WidgeDesk.Platform.Displays;
using WidgeDesk.Platform.Shell;
using WidgeDesk.UI.Controls;
using WidgeDesk.UI.Settings;
using WidgeDesk.UI.Theming;

namespace WidgeDesk.Dock;

/// <summary>
/// Dock 栏窗口。
/// </summary>
/// <remarks>
/// 结构：窗口只做三件事——摆放（<see cref="PositionOnDisplay"/>，主屏物理像素）、
/// 自动隐藏（<see cref="AutoHideController"/> 状态机 + 主屏窗口内的内容滑动）与把服务交给
/// <see cref="DockTileStrip"/>（磁贴、放大、滚动、命中、点击）。
/// 窗口在 <see cref="OnSourceInitialized"/> 里给自己挂上 <c>WS_EX_NOACTIVATE</c>：
/// 点击 Dock 不把用户正在使用的应用顶到后台。代价是 Dock 弹出的东西
/// （右键菜单、文件夹面板、窗口选择列表）也不会随点击取得焦点，需要键盘交互时
/// 必须显式调 <see cref="WindowActivation.TryBringToForeground"/>。
/// </remarks>
public partial class DockWindow : GlassWindow
{
    /// <summary>启动失败提示在状态行上停留的时长。之后恢复成常规诊断文字。</summary>
    private static readonly TimeSpan FailureNoticeDuration = TimeSpan.FromSeconds(5);

    /// <summary>自动隐藏与滑动的公共帧间隔。</summary>
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    private AppServiceHost? _services;
    private DockSettingsHost? _dockSettings;
    private Action<PixelRect?>? _toggleLauncher;
    /// <summary>宿主提供与桌面图标快捷键相同的动作，不依赖快捷键是否启用。</summary>
    public Action? ToggleDesktopIcons { get; set; }

    /// <summary>打开 WidgeDesk 设置中心；由宿主注入，Dock 不引用 App 工程。</summary>
    public Action? OpenSettings { get; set; }

    /// <summary>
    /// 通知区快照的来源；测试可替换，默认读真实系统。
    /// </summary>
    /// <remarks>
    /// 抽成可替换的委托，是为了让「后台程序会不会上 Dock」这件事能在没有
    /// 真实通知区图标的环境里断言——否则这条接线只能靠人工看界面验证。
    /// </remarks>
    public Func<IReadOnlyList<TrayApp>> ReadTrayApps { get; set; }
        = () => TrayAppScanner.Read((uint)Environment.ProcessId);
    private System.Windows.Threading.DispatcherTimer? _failureNoticeTimer;
    private readonly DockTileStrip _tileStrip;

    private readonly List<DockTileModel> _models = [];

    // ---- 自动隐藏（D-19） ----

    private AutoHideController? _autoHide;
    private System.Windows.Threading.DispatcherTimer? _frameTimer;
    private PixelRect _restingRect;
    private int _fullyHiddenOffsetPixels;
    private bool _revealLocked;
    private bool _edgeRevealedDuringSuppression;
    private bool _launcherContainerActive;
    private Rect? _slidePanelBoundsDip;
    private bool _backdropWasActive;
    private bool _panelWasConcealed;

    /// <summary>Keep the collapsed destination present until the expanded container has returned.</summary>
    public void SetLauncherContainerState(bool expanded)
    {
        _launcherContainerActive = expanded;
        if (expanded && !_performanceSleepRequested && _restingRect.Width > 0)
        {
            _autoHide?.ForceShown(DateTime.UtcNow);
            _slideOffsetPixels = 0;
            ApplySlide(_restingRect, 0);
        }
    }

    /// <summary>当前滑出量（物理像素）。0 = 完全在位；<see cref="SlideOutDistancePixels"/> = 完全隐藏。</summary>
    private double _slideOffsetPixels;

    internal bool IsOpticalSurfaceVisible => _slideOffsetPixels < Math.Max(1, _restingRect.Height - 4);
    internal PixelRect OpticalBackdropBounds => _restingRect;

    private DisplayInfo? CurrentPrimaryDisplay => PrimaryDisplaySession is { } session
        ? session.IsAvailable ? session.Display : null
        : DisplayService.Primary();

    protected override byte ResolveAcrylicTintAlpha(GlassPalette palette) => palette.IsDark ? (byte)54 : (byte)66;

    protected override Geometry CreatePanelGeometryCore(Rect bounds, double radius) =>
        SquircleGlassGeometry.Create(bounds, radius);

    protected override bool ContainsPanelGeometryCore(Rect bounds, double radius, Point point) =>
        SquircleGlassGeometry.Contains(_slidePanelBoundsDip ?? bounds, radius, point);

    protected override bool TryGetPanelGeometryEdgeCore(Rect bounds, double radius, Point point,
        out double depth, out Vector inwardNormal) =>
        SquircleGlassGeometry.TryGetEdge(_slidePanelBoundsDip ?? bounds, radius, point, out depth, out inwardNormal);

    protected override bool UsesSquirclePanelGeometryCore => true;

    public DockWindow()
    {
        InitializeComponent();
        InitializeEntryHover();
        PanelCornerRadius = new CornerRadius(40);
        GlassSurface.IsBackdropActive = () => IsOpticalSurfaceVisible;

        _tileStrip = new DockTileStrip();
        _tileStrip.TileClicked += OnTileClicked;
        _tileStrip.TileContextMenuRequested += OnTileContextMenuRequested;
        _tileStrip.ReorderFinished += (_, _) => RefreshTiles();
        _tileStrip.UnpinRequested += OnUnpinRequested;
        _tileStrip.SetReorderModel(new DockReorderModel(SaveDockOrder));
        TileStripHost.Content = _tileStrip;

        // 常驻的内置入口不依赖宿主：构造完就该出现在磁贴行上。
        RefreshTiles();
        InitializeTray();
    }

    /// <summary>
    /// 排序提交的落盘路径：只在这里写一次（R-08），并且**只写应用库里的真实条目**。
    /// </summary>
    /// <remarks>
    /// 排序模型给过来的列表本应只含应用磁贴，这里再校验一遍：
    /// 去掉内置入口（<c>entry:*</c>）、去掉库里不存在的 id、去重、保持顺序。
    /// 布局文件记的是「用户固定了哪些应用、什么顺序」，混进伪 id 就不再表示这件事了。
    /// </remarks>
    private void SaveDockOrder(IReadOnlyList<string> order)
    {
        if (_services is null)
        {
            return;
        }

        var sanitized = DockLayoutPolicy.Sanitize(order, id => _services.Library.Find(id) is not null);

        _services.Layouts.Dock.PinnedItemIds = sanitized;
        _services.Layouts.SaveDock();
    }

    /// <summary>
    /// 清理布局里不属于应用库的 id；有变化才写盘。
    /// </summary>
    /// <remarks>
    /// 早先的版本把三个内置入口（<c>entry:launcher</c> 等）写进了布局，已在文件里的那些
    /// 要在加载时清掉，同时**保持原有应用顺序**。
    ///
    /// <para>
    /// 安全前提：只有当应用库**确实加载出了条目**时才清洗。
    /// 库为空可能是加载失败，此时所有 id 都「查不到」，照着清洗会把整份布局抹掉。
    /// </para>
    /// </remarks>
    public void SanitizePinnedLayout()
    {
        if (_services is null || _services.Library.Items.Count == 0)
        {
            return;
        }

        var pinned = _services.Layouts.Dock.PinnedItemIds;
        var sanitized = DockLayoutPolicy.Sanitize(pinned, id => _services.Library.Find(id) is not null);

        if (DockLayoutPolicy.SameOrder(pinned, sanitized))
        {
            return;
        }

        var dropped = pinned.Count - sanitized.Count;
        _services.Layouts.Dock.PinnedItemIds = sanitized;
        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            AppendLayoutNotice("清理旧固定项时无法保存；磁盘布局已保留，启动继续。请检查数据目录的写入权限。");
            return;
        }

        AppendLayoutNotice($"布局文件里清理了 {dropped} 个不属于应用库的固定项（内置入口或已失效的条目）。");
    }

    /// <summary>
    /// 拖出取消固定（D-03）：只取消固定，<strong>不删</strong>应用库记录——
    /// 启动台里这一项还在。
    /// </summary>
    /// <remarks>
    /// 内置入口不受这一路径影响：它们不是固定项，也没有拖拽处理器。
    /// 这里再挡一道，防止将来的调用方误把 <c>entry:*</c> 递进来。
    /// </remarks>
    private void OnUnpinRequested(object? sender, string entryId)
    {
        if (_services is null || DockLayoutPolicy.IsEntryTile(entryId))
        {
            return;
        }

        if (!_services.Layouts.UnpinFromDock(entryId))
        {
            return;
        }

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存取消固定操作，原固定项已恢复。");
        }

        RefreshTiles();
    }

    /// <summary>把一条与布局有关的话写进启动日志，便于事后核对自愈行为。</summary>
    private void AppendLayoutNotice(string message)
    {
        if (_services is null)
        {
            return;
        }

        try
        {
            _services.Paths.EnsureCreated();
            File.AppendAllText(
                Path.Combine(_services.Paths.LogDirectory, "startup.log"),
                $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss}] Dock 布局：{message}{Environment.NewLine}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 记日志失败绝不能影响界面。
        }
    }

    /// <summary>悬停时接受可用的启动台应用条目或文件列表。</summary>
    private void OnExternalDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Effects = _services?.Library.Find(itemId) is { IsUsable: true }
                ? DragDropEffects.Copy
                : DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop, true)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    /// <summary>
    /// 外部拖入导入（D-01）：资源管理器拖来的文件走导入服务，
    /// 成功的条目顺手固定到 Dock 尾部——否则用户拖完了什么都没看见。
    /// </summary>
    /// <remarks>
    /// 注册归属（修订计划第 5 条）：WPF 对窗口句柄自己注册 OLE 拖放目标
    /// （探针 C 实测证实），这里只用 WPF 的 Drop 事件，绝不再 RegisterDragDrop。
    /// </remarks>
    private void OnExternalDrop(object sender, DragEventArgs e)
    {
        if (_services is null)
        {
            return;
        }

        if (e.Data.GetDataPresent(LauncherDragContract.SlotDataFormat)
            && e.Data.GetData(LauncherDragContract.SlotDataFormat) is string itemId)
        {
            e.Handled = true;
            PinLauncherItem(itemId, e);
            return;
        }

        if (!e.Data.GetDataPresent(DataFormats.FileDrop, true)) return;
        e.Handled = true;

        if (e.Data.GetData(DataFormats.FileDrop, true) is not string[] { Length: > 0 } sources)
        {
            return;
        }

        // 拖入期间自动隐藏计时被抑制，导入完成后恢复。
        _autoHide?.PushSuppress(DateTime.UtcNow);

        try
        {
            var requests = sources.Select(source => new ImportRequest { Source = source }).ToList();
            var result = _services.Import.ImportItems(requests);

            // 已固定的 id 与「本次新增/已在库」合并——重复条目也要固定：
            // 把一条从 Dock 移出的应用再拖回来时，导入报的是「重复」，
            // 只看 Added 会让这个手势完全没有反应（条目本来就在库里）。
            var pinned = _services.Layouts.Dock.PinnedItemIds;
            var merged = DockPinMerge.Merge(
                pinned,
                result.Added.Select(entry => entry.Id),
                result.Duplicates.Select(entry => entry.Id));

            var newlyPinned = merged.Count - pinned.Count;

            if (newlyPinned > 0)
            {
                _services.Layouts.Dock.PinnedItemIds = merged;
                try
                {
                    _services.Layouts.SaveDock();
                }
                catch (StorageException)
                {
                    ShowFailureNotice("已导入应用库，但未能保存 Dock 固定；原固定顺序已恢复。");
                    RefreshTiles();
                    return;
                }
            }

            ShowTemporaryNotice(result.Failures.Count == 0
                ? $"已固定 {newlyPinned} 项（新增 {result.Added.Count}，已在库 {result.Duplicates.Count}）。"
                : $"已固定 {newlyPinned} 项，{result.Failures.Count} 项失败。");

            RefreshTiles();
        }
        finally
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
        }
    }

    private void PinLauncherItem(string itemId, DragEventArgs e)
    {
        var entry = _services?.Library.Find(itemId);
        if (entry is not { IsUsable: true })
        {
            e.Effects = DragDropEffects.None;
            return;
        }

        var pinned = _services!.Layouts.Dock.PinnedItemIds;
        var merged = DockPinMerge.Merge(pinned, [entry.Id], []);
        if (merged.Count == pinned.Count)
        {
            ShowTemporaryNotice("此应用已在 Dock 中。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
            return;
        }

        _services.Layouts.Dock.PinnedItemIds = merged;
        try
        {
            _services.Layouts.SaveDock();
            ShowTemporaryNotice("已添加到 Dock。");
            e.Effects = DragDropEffects.Copy;
            RefreshTiles();
        }
        catch (StorageException)
        {
            ShowFailureNotice("未能保存 Dock 布局，原有图标顺序已恢复。");
            e.Effects = DragDropEffects.None;
            RefreshTiles();
        }
    }

    /// <summary>生效中的程序坞设置；尚未附加宿主时是默认值。</summary>
    public DockSettings Settings => _dockSettings?.Settings ?? new DockSettings();

    /// <summary>
    /// 当前磁贴总数（固定项 + 三个内置入口）。诊断与测试用。
    /// </summary>
    public int TileCount => _models.Count;

    /// <summary>当前磁贴模型，按显示顺序。测试用它断言磁贴行的构成。</summary>
    /// <remarks>
    /// 只读视图：调用方拿不到内部列表本身，改不了 Dock 的状态。
    /// </remarks>
    public IReadOnlyList<DockTileModel> TileModels => _models;

    /// <summary>
    /// 把宿主建好的东西交给 Dock：应用服务入口、程序坞设置通知口与「切换启动台」回调。
    /// </summary>
    /// <remarks>
    /// 结构约束（开工说明 4.7）：Dock 经 <see cref="AppServiceHost"/> 使用应用服务，
    /// 绝不引用宿主工程 <c>WidgeDesk.App</c>；设置变更经
    /// <see cref="DockSettingsHost.Changed"/> 推进来，Dock 不轮询设置文件；
    /// 运行状态经 <see cref="RunningAppTracker.Changed"/> 推进来，
    /// Dock 不做任何窗口查询。
    /// </remarks>
    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(toggleLauncher);
        AttachServices(services, settings, _ => toggleLauncher());
    }

    public void AttachServices(AppServiceHost services, DockSettingsHost settings, Action<PixelRect?> toggleLauncher)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(toggleLauncher);

        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        _services = services;
        _dockSettings = settings;
        _toggleLauncher = toggleLauncher;

        _dockSettings.Changed += OnDockSettingsChanged;
        _services.Running.Changed += OnRunningChanged;

        // 布局里可能残留旧版本写进去的内置入口（entry:*）或已经不存在的 id，
        // 在第一次画磁贴之前清掉。
        SanitizePinnedLayout();

        RebuildAutoHide();
        RefreshTiles();
        RefreshFromSettings();
    }

    /// <summary>Raise the non-activating Dock above Launcher while it is a drag target.</summary>
    public void RaiseAboveLauncherDuringDrag() => _ = WindowStyles.BringToTopWithoutActivation(Handle);

    /// <summary>Dock 使用栏自身的圆角半径，而不是启动台的。</summary>
    protected override double ResolveCornerRadius(AppearanceSettings appearance)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        return Math.Max(40, appearance.DockCornerRadius) * DockScale;
    }

    // ---- 磁贴 ----

    /// <summary>
    /// 按布局文档的固定顺序重建磁贴行。
    /// </summary>
    /// <remarks>
    /// 固定顺序来自 <c>dock-layout.json</c> 的稠密有序列表：位置即索引。
    /// 布局里引用了但库里已经没有的 id 直接跳过——那只会发生在数据被
    /// 手工编辑过的时候，正常路径上「从 Dock 移除」会同步更新布局。
    /// </remarks>
    public void RefreshTiles()
    {
        if (_performanceSleepRequested) { _tilesDirty = true; return; }
        var settings = Settings;
        var tracker = _services?.Running;
        var dotBrush = ThemeService.Current?.Palette.ForegroundBrush;

        _models.Clear();

        // 固定项依赖应用服务；未附加宿主时（构造后立刻查询的场合）只少固定项，
        // 内置入口独立于固定项，由 Dock 设置控制可见性。
        if (_services is not null)
        {
            foreach (var id in _services.Layouts.Dock.PinnedItemIds)
            {
                var entry = _services.Library.Find(id);

                if (entry is null || !entry.IsUsable)
                {
                    continue;
                }

                _models.Add(CreateTile(entry, settings, tracker!, dotBrush));
            }

            // 自动磁贴排在固定项之后：用户自己排好的顺序不受运行情况影响。
            foreach (var running in BuildAutoTiles())
            {
                _models.Add(CreateAutoTile(running, settings, dotBrush));
            }
        }

        _tileStrip.SetTiles(_models);
        ApplyStripMetrics();
    }

    /// <summary>
    /// 此刻应当自动出现在 Dock 上的运行中应用。
    /// </summary>
    /// <remarks>
    /// 排除两类：已经固定的（避免同一应用出现两枚图标），以及用户明确移除过的
    /// （见 <see cref="LayoutStore.SuppressAutoItem"/>）。
    /// </remarks>
    private IReadOnlyList<RunningApp> BuildAutoTiles()
    {
        if (_services is null)
        {
            return [];
        }

        var covered = _services.Layouts.Dock.PinnedItemIds.ToHashSet(StringComparer.Ordinal);
        var suppressed = _services.Layouts.Dock.SuppressedAutoItemIds;
        var windowApps = _services.Running.BuildAutoTiles(covered, suppressed);

        // 再补上只活在通知区里的后台程序：它们没有顶层窗口，
        // 上一条基于窗口快照的路径永远看不到。
        var trayApps = ReadTrayApps();
        if (trayApps.Count == 0) return windowApps;

        var background = BackgroundAppCatalog.Build(
            trayApps,
            _services.Library,
            covered,
            [.. windowApps.Select(app => app.Target)],
            suppressed,
            (uint)Environment.ProcessId);

        return background.Count == 0 ? windowApps : [.. windowApps, .. background];
    }

    /// <summary>把一条运行中的应用造成磁贴模型。</summary>
    private DockTileModel CreateAutoTile(RunningApp running, DockSettings settings, Brush? dotBrush)
    {
        var scale = DockScale;

        return new DockTileModel
        {
            EntryId = running.Id,
            IsAuto = true,
            LaunchEntryId = running.EntryId,
            LaunchTarget = running.Target,
            Aumid = running.Aumid,
            AutoIconSource = running.IconSource,
            AutoCustomIconPath = running.CustomIconPath,
            Windows = running.Windows,
            WindowTitles = running.WindowTitles,
            DisplayName = running.DisplayName,
            ToolTipText = running.DisplayName,
            Icon = LoadAutoTileIcon(running),
            DotBrush = dotBrush,
            IconSize = DockSettings.FixedIconSize * scale,
            TileMargin = new Thickness(DockSettings.FixedIconSpacing * scale / 2, 0, DockSettings.FixedIconSpacing * scale / 2, 0),
            // 自动磁贴代表的就是「它正在运行」，状态点常亮——这正是它出现在这里的原因。
            RunningVisibility = settings.ShowRunningIndicator ? Visibility.Visible : Visibility.Collapsed,
        };
    }

    /// <summary>取自动磁贴的图标：用户自选图标优先，其次图标缓存。</summary>
    /// <remarks>
    /// 与库条目磁贴（<see cref="LoadTileIcon"/>）同一条优先级：用户特意给这个应用换过图标，
    /// 自动出现的磁贴当然也该用它，否则同一个应用在固定前后长得不一样。
    /// </remarks>
    private BitmapImage? LoadAutoTileIcon(RunningApp running)
    {
        if (_services is null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(running.CustomIconPath) && File.Exists(running.CustomIconPath))
        {
            return LoadBitmap(running.CustomIconPath);
        }

        if (string.IsNullOrWhiteSpace(running.IconSource))
        {
            return null;
        }

        var cached = _services.Icons.PathFor(running.IconSource);

        if (!File.Exists(cached))
        {
            cached = _services.Icons.EnsureFor(running.IconSource)?.Path ?? cached;
        }

        return File.Exists(cached) ? LoadBitmap(cached) : null;
    }

    private DockTileModel CreateTile(AppEntry entry, DockSettings settings, RunningAppTracker tracker, Brush? dotBrush)
    {
        var tooltip = entry.DisplayName;

        if (entry.IsTargetMissing)
        {
            // 失效条目（R-07）现在先在提示里说清楚；「显示为失效」的视觉
            // 与右键重新定位入口在批次 5 接入。
            tooltip += "（目标已不在原处，可重新定位）";
        }

        return new DockTileModel
        {
            EntryId = entry.Id,
            DisplayName = entry.DisplayName,
            ToolTipText = tooltip,
            Icon = LoadTileIcon(entry),
            DotBrush = dotBrush,
            IconSize = DockSettings.FixedIconSize * DockScale,
            TileMargin = new Thickness(DockSettings.FixedIconSpacing * DockScale / 2, 0, DockSettings.FixedIconSpacing * DockScale / 2, 0),
            IsMissing = entry.IsTargetMissing,
            RunningVisibility = tracker.IsRunning(entry)
                ? Visibility.Visible
                : Visibility.Collapsed,
        };
    }

    /// <summary>
    /// 把尺寸与滚动封顶交给磁贴条。
    /// </summary>
    /// <remarks>
    /// 封顶宽度 = 目标显示器工作区的 DIP 宽度 − 面板内边距、已启用入口与放大余量。
    /// 固定项超出时磁贴条自身横向滚动（修订计划第 6 条），窗口永远不出工作区。
    /// </remarks>
    private void ApplyStripMetrics()
    {
        var settings = Settings;
        var display = CurrentPrimaryDisplay;
        var scale = DockScale;
        DockContentLayer.Margin = new Thickness(10 * scale, 8 * scale, 10 * scale, 8 * scale);
        if ((LauncherButton.Visibility == Visibility.Visible) != settings.ShowLauncherEntry
            || (ThisPcButton.Visibility == Visibility.Visible) != settings.ShowThisPcEntry
            || (DesktopIconsButton.Visibility == Visibility.Visible) != settings.ShowDesktopIconsEntry
            || (SettingsButton.Visibility == Visibility.Visible) != settings.ShowSettingsEntry)
            ResetEntryHover();
        LauncherButton.Visibility = settings.ShowLauncherEntry ? Visibility.Visible : Visibility.Collapsed;
        ThisPcButton.Visibility = settings.ShowThisPcEntry ? Visibility.Visible : Visibility.Collapsed;
        DesktopIconsButton.Visibility = settings.ShowDesktopIconsEntry ? Visibility.Visible : Visibility.Collapsed;
        SettingsButton.Visibility = settings.ShowSettingsEntry ? Visibility.Visible : Visibility.Collapsed;
        foreach (var button in new[] { LauncherButton, ThisPcButton, DesktopIconsButton, SettingsButton })
        {
            button.Width = 48 * scale;
            button.Height = 64 * scale;
            button.Margin = new Thickness(0, 0, 8 * scale, 0);
        }
        LaunchpadTile.Width = LaunchpadTile.Height = ThisPcTile.Width = ThisPcTile.Height
            = DesktopIconsTile.Width = DesktopIconsTile.Height
            = SettingsTile.Width = SettingsTile.Height = 48 * scale;
        LaunchpadTile.CornerRadius = new CornerRadius(16 * scale);
        PanelCornerRadius = new CornerRadius(40 * scale);

        // 托盘图标比应用图标小：它们表示系统状态，不该和可启动的应用争夺注意力。
        foreach (var button in new[] { NetworkButton, VolumeButton })
        {
            button.Width = 30 * scale;
            button.Height = 34 * scale;
            button.Padding = new Thickness(0);
        }
        NetworkTile.Width = NetworkTile.Height = VolumeTile.Width = VolumeTile.Height = 20 * scale;
        ClockButton.Padding = new Thickness(6 * scale, 0, 2 * scale, 0);
        ClockButton.Height = 34 * scale;
        ClockTime.FontSize = 12 * scale;
        ClockDate.FontSize = 9 * scale;

        var contentLimit = display is null
            ? 0.0
            : Math.Max(1, display.ToDips((int)display.WorkArea.Width) - 32
                - (28 + BuiltinEntryWidthDip) * scale - TrayWidthDip);

        if (settings.FrameLengthDip > 0)
            contentLimit = Math.Max(1, ResolveEffectiveFrameLengthDip(settings.FrameLengthDip, display)
                - (22 + BuiltinEntryWidthDip) * scale - TrayWidthDip);

        _tileStrip.SetMetrics(
            DockSettings.FixedIconSize * scale,
            DockSettings.FixedIconSpacing * scale,
            DockSettings.FixedMagnificationPeak,
            8 * scale,
            contentLimit);
    }

    private double DockScale => double.IsFinite(Settings.SizeScale)
        ? Math.Clamp(Settings.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;

    private double BuiltinEntryWidthDip => 56 * ((Settings.ShowLauncherEntry ? 1 : 0)
        + (Settings.ShowThisPcEntry ? 1 : 0) + (Settings.ShowDesktopIconsEntry ? 1 : 0)
        + (Settings.ShowSettingsEntry ? 1 : 0));

    private double ResolveEffectiveFrameLengthDip(double requested, DisplayInfo? display)
    {
        var entryMinimum = Math.Max(DockSettings.MinimumFrameLengthDip, BuiltinEntryWidthDip + 22) * DockScale;
        if (display is null)
            return Math.Clamp(requested, entryMinimum, DockSettings.MaximumFrameLengthDip);
        var maxWidth = Math.Max(64, display.ToDips((int)display.WorkArea.Width) - 32);
        maxWidth = Math.Min(DockSettings.MaximumFrameLengthDip, maxWidth);
        var minimum = Math.Min(entryMinimum, maxWidth);
        return Math.Clamp(requested, minimum, maxWidth);
    }

    private void ApplyFrameLength()
    {
        var requested = Settings.FrameLengthDip;
        if (requested <= 0)
        {
            SizeToContent = System.Windows.SizeToContent.WidthAndHeight;
            Width = double.NaN;
            return;
        }

        var display = CurrentPrimaryDisplay;
        SizeToContent = System.Windows.SizeToContent.Height;
        Width = ResolveEffectiveFrameLengthDip(requested, display);
    }

    /// <summary>取一枚磁贴的图标：用户自选的优先，其次缓存里提取的，没有就空着。</summary>
    private BitmapImage? LoadTileIcon(AppEntry entry)
    {
        var custom = entry.CustomIconPath;

        if (!string.IsNullOrEmpty(custom) && File.Exists(custom))
        {
            return LoadBitmap(custom);
        }

        if (_services is null || string.IsNullOrEmpty(entry.IconSource))
        {
            return null;
        }

        var cached = _services.Icons.PathFor(entry.IconSource);
        if (!File.Exists(cached))
        {
            cached = _services.Icons.EnsureFor(entry.IconSource)?.Path ?? cached;
        }

        return File.Exists(cached) ? LoadBitmap(cached) : null;
    }

    private static BitmapImage? LoadBitmap(string path)
    {
        try
        {
            // OnLoad：把像素读进内存就释放文件句柄，否则用户在资源管理器里
            // 删不掉正在被 Dock 显示的那张图标。
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.UriSource = new Uri(path, UriKind.Absolute);
            image.EndInit();
            image.Freeze();
            return image;
        }
        catch (Exception ex) when (ex is IOException or UriFormatException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    /// <summary>磁贴点击（D-08）：内置入口分派；文件夹条目弹面板（D-11）；其余先激活后启动。</summary>
    private void OnTileClicked(object? sender, string entryId)
    {
        if (entryId.StartsWith("entry:", StringComparison.Ordinal))
        {
            RunEntryAction(entryId["entry:".Length..]);
            return;
        }

        if (_services is null)
        {
            return;
        }

        // 自动磁贴：它代表一个正在运行的应用，点击的意图是「把它调到前面」。
        // 对应库条目存在时走激活；不存在（例如库外的后台进程）时按目标老实启动，
        // 由应用自己决定是开新窗口还是前置已有窗口。
        if (RunningAppCatalog.IsAutoTile(entryId))
        {
            ActivateAutoTile(entryId);
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is not null && entry.Kind == AppItemKind.Folder && !entry.IsTargetMissing)
        {
            ShowFolderPanel(entry);
            return;
        }

        // 用调和快照而不是现场枚举：状态点依据的就是这份快照，
        // 点击再枚举一次会让「图标亮着却新开一个」成为可能。
        var outcome = _services.Launch.ActivateOrLaunch(entryId, _services.Running.LastWindows);

        switch (outcome.Outcome)
        {
            case ActivationOutcome.MultipleWindowsFound:
                ShowWindowChooser(outcome.Candidates);
                break;

            case ActivationOutcome.Failed:
                ShowFailureNotice(outcome.Message);
                break;

            default:
                // Activated / Launched 不需要额外反馈：窗口到前台或应用启动本身就是反馈。
                break;
        }
    }

    /// <summary>
    /// 激活一枚自动磁贴：把它**已经存在的窗口**提到前面，绝不重新启动。
    /// </summary>
    /// <remarks>
    /// <b>这是「与任务栏一致」的核心。</b>自动磁贴只因为「这个应用此刻有窗口」才存在，
    /// 所以点它的时候窗口一定就在那里——再去调一次启动，等于要求应用自己判断
    /// 「我已经开着了吗」，而多数程序不做这个判断，于是多开一个。
    /// 之前正是这么写的，表现为「点一次多一个」。
    ///
    /// <para>
    /// 只有一个窗口就前置它；有多个则让用户选——这与任务栏点分组图标的行为一致。
    /// 只有在磁贴记录下来的窗口全部失效（应用刚退出、状态还没调和）时才回退到启动，
    /// 那是一种竞态兜底，而不是常规路径。
    /// </para>
    /// </remarks>
    private void ActivateAutoTile(string autoId)
    {
        if (_services is null)
        {
            return;
        }

        var model = _models.FirstOrDefault(tile => string.Equals(tile.EntryId, autoId, StringComparison.Ordinal));

        if (model is null)
        {
            return;
        }

        // 先按磁贴记下的句柄激活。这些句柄就是状态点所依据的那批窗口，
        // 因此「有点就是有窗口」这件事在这里必然成立。
        var alive = model.Windows.Where(WindowControl.IsAlive).ToList();

        switch (RunningAppCatalog.DecideActivation(alive.Count))
        {
            case RunningAppCatalog.Activation.Raise:
                ActivateWindowOrReport(alive[0], model.DisplayName);
                return;

            case RunningAppCatalog.Activation.Choose:
                ShowWindowChooser(CandidatesFrom(model, alive));
                return;
        }

        // 到这里说明窗口已在调和之前关掉了。库条目存在时走它（会重新匹配/启动），
        // 否则按目标启动——此时确实没有可前置的窗口。
        if (model.LaunchEntryId is { } entryId && _services.Library.Find(entryId) is not null)
        {
            OnTileClicked(this, entryId);
            return;
        }

        if (string.IsNullOrWhiteSpace(model.LaunchTarget))
        {
            return;
        }

        var opened = ShellExecutor.Open(model.LaunchTarget, null, null);

        if (!opened.Started)
        {
            ShowFailureNotice($"{model.DisplayName} 未能打开。");
        }
    }

    /// <summary>把磁贴记下的存活窗口换算成选择列表的候选。</summary>
    private static List<WindowCandidate> CandidatesFrom(DockTileModel model, List<nint> alive)
    {
        var candidates = new List<WindowCandidate>(alive.Count);

        foreach (var handle in alive)
        {
            var index = IndexOf(model.Windows, handle);
            var title = index >= 0 && index < model.WindowTitles.Count ? model.WindowTitles[index] : string.Empty;
            candidates.Add(new WindowCandidate { Handle = handle, Title = title });
        }

        return candidates;
    }

    private static int IndexOf(IReadOnlyList<nint> handles, nint handle)
    {
        for (var index = 0; index < handles.Count; index++)
        {
            if (handles[index] == handle) return index;
        }

        return -1;
    }

    /// <summary>前置一个窗口；失败时如实说明，而不是偷偷再启动一个。</summary>
    private void ActivateWindowOrReport(nint handle, string displayName)
    {
        var outcome = LaunchService.ActivateWindow(handle);

        if (outcome.Outcome != ActivationOutcome.Activated)
        {
            ShowFailureNotice(outcome.Message ?? $"{displayName} 的窗口没能切到前台。");
        }
    }

    /// <summary>内置入口的动作分派。</summary>
    private void RunEntryAction(string name)
    {
        switch (name)
        {
            case "launcher":
                // D-12：入口磁贴触发宿主切换回调（Dock 不直接持有启动台）。
                RequestLauncherToggle();
                break;

            case "showdesktop":
                // D-13：实现依据探针 D 的实测结论（逐窗口 SW_MINIMIZE）。
                _ = DesktopCommands.ShowDesktop();
                break;

            case "recyclebin":
                // D-14：回收站是内置入口，直接走外壳；绝不为塞进应用库而
                // 放宽导入的存在性检查（阶段 4 开工说明 4.6 的明确告诫）。
                var opened = ShellExecutor.Open("shell:RecycleBinFolder", null, null);
                if (!opened.Started)
                {
                    ShowFailureNotice("回收站没能打开。");
                }

                break;

            default:
                break;
        }
    }

    /// <summary>
    /// 文件夹弹出面板（D-11）：列出文件夹里的条目，点击经外壳打开。
    /// </summary>
    /// <remarks>
    /// 面板打开时取得键盘焦点，支持 Esc；关闭时只在焦点仍属于 Dock 的情况下
    /// 归还原窗口，不抢回用户已切换到的其他应用。面板期间抑制自动隐藏。
    /// 面板里的点击走同一条外壳路径（<see cref="ShellExecutor"/>）。
    /// </remarks>
    private void ShowFolderPanel(AppEntry folderEntry)
    {
        _autoHide?.PushSuppress(DateTime.UtcNow);
        var foregroundBefore = WindowControl.GetForegroundWindow();

        var palette = ThemeService.Current?.Palette;

        var header = new TextBlock
        {
            Text = folderEntry.DisplayName,
            FontWeight = FontWeights.Medium,
            Margin = new Thickness(6, 4, 6, 2),
        };

        var list = new StackPanel();

        _ = list.Children.Add(header);
        _ = list.Children.Add(PanelItem("打开这个文件夹", () =>
        {
            var open = ShellExecutor.Open(folderEntry.Target, null, null);
            if (!open.Started)
            {
                ShowFailureNotice("文件夹没能打开。");
            }
        }));

        string[] children;

        try
        {
            children = Directory
                .EnumerateFileSystemEntries(folderEntry.Target)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCulture)
                .Take(50)
                .ToArray();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            children = [];
        }

        if (children.Length == 0)
        {
            _ = list.Children.Add(new TextBlock
            {
                Text = "（空文件夹，或读不到内容）",
                Margin = new Thickness(10, 4, 10, 6),
            });
        }

        foreach (var child in children)
        {
            var path = child;
            _ = list.Children.Add(PanelItem(Path.GetFileName(path), () =>
            {
                var open = ShellExecutor.Open(path, null, null);
                if (!open.Started)
                {
                    ShowFailureNotice($"没能打开：{Path.GetFileName(path)}");
                }
            }));
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 220,
            MaxHeight = 420,
            Child = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
        };

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        panel.Focusable = true;
        panel.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Escape) return;
            popup.IsOpen = false;
            args.Handled = true;
        };
        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, 0);
        };
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = panel.Focus();
    }

    private static TextBlock PanelItem(string text, Action onClick)
    {
        var item = new TextBlock
        {
            Text = text,
            Padding = new Thickness(10, 5, 10, 5),
            Cursor = System.Windows.Input.Cursors.Hand,
        };

        item.MouseLeftButtonUp += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 磁贴右键菜单（D-04）：菜单项与实际行为一致，不放占位项。
    /// </summary>
    /// <remarks>
    /// 主栏带 <c>WS_EX_NOACTIVATE</c>，不激活就点不到菜单外部——菜单自动收起
    /// 需要「点击外部时收起」的语义，所以打开前显式把 Dock 调到前台。
    /// 关闭后延后检查前台，仅在仍属于本弹层/主栏时归还原窗口。
    /// 失效条目（R-07）在这里给出「重新定位」入口。
    /// </remarks>
    private void OnTileContextMenuRequested(object? sender, string entryId)
    {
        if (_services is null)
        {
            return;
        }

        // 自动磁贴：菜单只有「退出程序」与「从 Dock 移除」两项。它不在应用库里，
        // 「打开」「重新定位」这些针对库条目的动作对它没有意义。
        if (RunningAppCatalog.IsAutoTile(entryId))
        {
            ShowAutoTileContextMenu(entryId);
            return;
        }

        var entry = _services.Library.Find(entryId);

        if (entry is null)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
        };
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;

        _ = menu.Items.Add(MenuItem("打开", () => OnTileClicked(this, entryId)));

        if (entry.Kind == AppItemKind.Folder)
        {
            _ = menu.Items.Add(MenuItem("在面板中查看内容", () => ShowFolderPanel(entry)));
        }

        _ = menu.Items.Add(new Separator());

        if (entry.IsTargetMissing)
        {
            // R-07 的界面入口：目标被删之后用户从这里把条目指到新位置。
            _ = menu.Items.Add(MenuItem("重新定位…", () => RelocateFromDialog(entry)));
        }

        _ = menu.Items.Add(MenuItem("从 Dock 移除", () => OnUnpinRequested(this, entryId)));

        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 自动磁贴的右键菜单：打开（前置）、退出程序、从 Dock 移除。
    /// </summary>
    /// <remarks>
    /// 与库条目磁贴共用同一套前台处理：主栏带 <c>WS_EX_NOACTIVATE</c>，
    /// 打开菜单前必须显式把它调到前台，否则点击菜单外部不会收起菜单。
    /// </remarks>
    private void ShowAutoTileContextMenu(string autoId)
    {
        if (_services is null)
        {
            return;
        }

        var model = _models.FirstOrDefault(tile => string.Equals(tile.EntryId, autoId, StringComparison.Ordinal));

        if (model is null)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        var menu = new ContextMenu
        {
            Placement = PlacementMode.MousePoint,
        };
        var foregroundBefore = WindowControl.GetForegroundWindow();
        nint menuHandle = 0;
        menu.Opened += (_, _) => menuHandle = (PresentationSource.FromVisual(menu) as System.Windows.Interop.HwndSource)?.Handle ?? 0;

        _ = menu.Items.Add(MenuItem("打开", () => ActivateAutoTile(autoId)));

        // 一个窗口都没有的自动项不该出现（它正是因为有窗口才在这里），
        // 但真出现了也不给一个点了没反应的菜单项。
        if (model.Windows.Count > 0)
        {
            _ = menu.Items.Add(MenuItem("退出程序", () => QuitAutoTile(model)));
        }

        _ = menu.Items.Add(new Separator());
        _ = menu.Items.Add(MenuItem("从 Dock 移除", () => RemoveAutoTile(model)));

        menu.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);
            RestorePopupForeground(foregroundBefore, menuHandle);
        };

        _ = WindowActivation.TryBringToForeground(Handle);
        menu.IsOpen = true;
    }

    /// <summary>
    /// 退出这个应用：对它的每个窗口投递 <c>WM_CLOSE</c>。
    /// </summary>
    /// <remarks>
    /// <b>逐窗口关闭，不结束进程。</b>关闭走的是窗口自己的流程，因此「是否保存」
    /// 这类询问会照常出现；按进程结束会直接丢掉用户的未保存内容。
    ///
    /// <para>
    /// 不在这里等结果：应用可能要几秒才退干净（弹了保存询问就更久）。
    /// 图标由下一次窗口事件触发的调和自动收走——这正是「彻底关闭后自动消失」的实现。
    /// 为了让用户马上看到反馈，紧接着主动调和一次窗口集合。
    /// </para>
    /// </remarks>
    private void QuitAutoTile(DockTileModel model)
    {
        var closed = 0;

        foreach (var window in model.Windows)
        {
            if (WindowControl.Close(window))
            {
                closed++;
            }
        }

        if (closed == 0)
        {
            ShowFailureNotice($"{model.DisplayName} 已经没有可关闭的窗口。");
            _services?.Running.Reconcile();
            return;
        }

        ShowTemporaryNotice($"正在退出 {model.DisplayName}…");

        // 关闭是异步的：让窗口事件有机会先到，再调和一次兜底——
        // 某些应用的窗口消失不会触发我们监听的事件。
        _services?.Running.Reconcile();
    }

    /// <summary>
    /// 把一枚自动磁贴从 Dock 上移除。
    /// </summary>
    /// <remarks>
    /// 必须记下「用户移除过它」，否则下一次调和发现应用还在运行会立刻把图标加回来，
    /// 用户看到的是菜单点了没反应。应用真的关掉之后这条记录自动作废。
    ///
    /// <para>
    /// 只影响自动项：用户亲手固定的图标走的是 <see cref="OnUnpinRequested"/>，
    /// 两者互不干扰。
    /// </para>
    /// </remarks>
    private void RemoveAutoTile(DockTileModel model)
    {
        if (_services is null)
        {
            return;
        }

        _services.Layouts.SuppressAutoItem(model.EntryId);

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            // 写不进去时它可能在下一次调和后重新出现，如实提示而不是假装成功。
            ShowFailureNotice("未能记住这次移除；应用仍在运行时它可能再次出现。");
        }

        RefreshTiles();
    }

    /// <summary>重新定位对话框：选一个真实存在的新目标，交给维护服务。</summary>
    private void RelocateFromDialog(AppEntry entry)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = $"重新定位「{entry.DisplayName}」",
            CheckFileExists = false,
        };

        if (dialog.ShowDialog(this) != true || string.IsNullOrWhiteSpace(dialog.FileName))
        {
            return;
        }

        var outcome = _services!.Maintenance.Relocate(entry.Id, dialog.FileName);

        if (outcome.Result != RelocateResult.Relocated)
        {
            ShowFailureNotice(outcome.Message);
        }

        RefreshTiles();
    }

    private static MenuItem MenuItem(string header, Action onClick)
    {
        var item = new MenuItem { Header = header };
        item.Click += (_, _) => onClick();
        return item;
    }

    /// <summary>
    /// 多窗口选择列表（D-10）：列出候选窗口的标题，点哪个激活哪个。
    /// </summary>
    /// <remarks>
    /// <para>
    /// 交互（修订 4 的焦点规则在这里落地）：
    /// <list type="bullet">
    /// <item>点一行 → 激活那一行的窗口；<b>句柄要从所属列表项取</b>，
    /// 不能只读点击来源的 Tag（来源往往是里面的文字，自己没有 Tag，
    /// 于是点了没反应也不报错——见 <see cref="WindowChooserHit"/>）；</item>
    /// <item>Enter → 激活选中行；Esc → 关闭（等同取消）；</item>
    /// <item>主栏带 <c>WS_EX_NOACTIVATE</c>，弹层要拿键盘焦点必须显式把自己调到前台；
    /// 因此打开时记下当时的前台窗口，<b>取消（Esc / 点外部）时还回去</b>，
    /// 而选中并激活成功时焦点自然留在目标应用上。</item>
    /// </list>
    /// </para>
    /// </remarks>
    private void ShowWindowChooser(IReadOnlyList<WindowCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return;
        }

        _autoHide?.PushSuppress(DateTime.UtcNow);

        // 打开之前的前台窗口：取消时还给它，避免「关掉列表后焦点不知所踪」。
        var foregroundBefore = WindowControl.GetForegroundWindow();
        var activated = false;

        var palette = ThemeService.Current?.Palette;

        var list = new ListBox
        {
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
        };

        for (var index = 0; index < candidates.Count; index++)
        {
            var candidate = candidates[index];
            var title = string.IsNullOrWhiteSpace(candidate.Title)
                ? $"窗口 {index + 1}"
                : candidate.Title;

            var item = new ListBoxItem
            {
                Content = title,
                Padding = new Thickness(10, 5, 10, 5),
                Tag = candidate.Handle,
            };

            list.Items.Add(item);
        }

        if (list.Items.Count > 0)
        {
            list.SelectedIndex = 0;
        }

        var panel = new PhoneBorder
        {
            Background = palette?.ResolvePanelBrush(false) ?? Brushes.DimGray,
            BorderBrush = palette?.PanelBorderBrush ?? Brushes.Gray,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(4),
            MinWidth = 160,
        };

        panel.Child = list;

        var popup = new Popup
        {
            Child = panel,
            Placement = PlacementMode.MousePoint,
            StaysOpen = false,
            AllowsTransparency = true,
        };

        // 选中并激活某一行的窗口。返回是否真的激活成功（用于决定要不要还焦点）。
        bool Activate(nint handle)
        {
            popup.IsOpen = false;

            var outcome = LaunchService.ActivateWindow(handle);

            if (outcome.Outcome == ActivationOutcome.Activated)
            {
                activated = true;
                return true;
            }

            // 窗口已经关掉、或系统拒绝前置：不能静默，状态行上要说清楚。
            ShowFailureNotice(outcome.Message ?? "没能把那个窗口切到前台。");
            return false;
        }

        list.PreviewMouseLeftButtonUp += (_, args) =>
        {
            var handle = WindowChooserHit.Resolve(list, args.OriginalSource);

            if (handle != 0)
            {
                _ = Activate(handle);
            }
        };

        list.KeyDown += (_, args) =>
        {
            switch (args.Key)
            {
                case System.Windows.Input.Key.Enter:
                    if (list.SelectedItem is ListBoxItem { Tag: nint selected } && selected != 0)
                    {
                        _ = Activate(selected);
                    }

                    args.Handled = true;
                    break;

                case System.Windows.Input.Key.Escape:
                    popup.IsOpen = false;
                    args.Handled = true;
                    break;

                default:
                    break;
            }
        };

        popup.Closed += (_, _) =>
        {
            _autoHide?.PopSuppress(DateTime.UtcNow);

            // 取消（没激活任何窗口）时把前台还给打开前的那个窗口；
            // 激活成功时焦点已经属于目标应用，不要再抢回来。
            if (!activated)
            {
                RestorePopupForeground(foregroundBefore, 0);
            }
        };

        // 主栏不激活，弹层要收键盘就得显式取前台；随后把焦点交给列表。
        _ = WindowActivation.TryBringToForeground(Handle);
        popup.IsOpen = true;
        _ = list.Focus();
    }

    // Closed can run before the menu action or window activation completes. Defer the
    // decision, and restore only if focus is still ours. Clicking another app must win.
    private void RestorePopupForeground(nint previous, nint popupHandle)
    {
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ContextIdle, new Action(() =>
        {
            if (!WindowControl.IsAlive(previous)) return;
            var current = WindowControl.GetForegroundWindow();
            if (current == Handle || current == 0 || (popupHandle != 0 && current == popupHandle))
                _ = WindowActivation.TryBringToForeground(previous);
        }));
    }

    /// <summary>启动失败不能没有声音地过去：在状态行上亮出原因，几秒后自己恢复。</summary>
    private void ShowFailureNotice(string? message)
    {
        ShowTemporaryNotice(message);
    }

    private void ShowTemporaryNotice(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        StatusText.Text = message;
        StatusText.Visibility = Visibility.Visible;

        _failureNoticeTimer ??= NewFailureNoticeTimer();
        _failureNoticeTimer.Stop();
        _failureNoticeTimer.Start();
    }

    private System.Windows.Threading.DispatcherTimer NewFailureNoticeTimer()
    {
        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FailureNoticeDuration,
        };

        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RefreshDiagnostics();
            RefreshFromSettings();
        };

        return timer;
    }

    // ---- 运行状态点（D-07） ----

    private void OnRunningChanged(object? sender, EventArgs e)
    {
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        // 调和在线程池上触发；圆点更新回到界面线程。只改可见性，不重建磁贴。
        _ = Dispatcher.BeginInvoke(() =>
        {
            if (_services is null || _performanceSleepRequested)
            {
                return;
            }

            foreach (var tile in _models)
            {
                // 自动磁贴代表的就是「正在运行」，它的状态点常亮，不参与这一轮判定。
                if (tile.IsAuto)
                {
                    continue;
                }

                var entry = _services.Library.Find(tile.EntryId);

                tile.RunningVisibility = entry is not null && _services.Running.IsRunning(entry)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
            }

            // 自动磁贴的增删要重建整行（新应用出现、旧应用彻底关掉），
            // 而上面那种只改可见性的做法覆盖不了。空闲时再重建，避免拖拽或动画中途换控件。
            SyncAutoTiles();
        });
    }

    /// <summary>
    /// 把自动磁贴对齐到当前真实的运行情况：多出来的加进来，已经关掉的去掉。
    /// </summary>
    /// <remarks>
    /// <b>只在集合真的变了时才重建。</b>调和由窗口事件触发，而窗口标题变化、
    /// 焦点切换都可能触发它；每次都重建整行会让图标闪烁、正在放大的动画被打断。
    /// 比较用槽位 id 的有序序列：内容与顺序都一样就什么都不做。
    /// </remarks>
    private void SyncAutoTiles()
    {
        if (_services is null || _performanceSleepRequested)
        {
            return;
        }

        var desired = BuildAutoTiles().Select(app => app.Id).ToList();
        var current = _models.Where(tile => tile.IsAuto).Select(tile => tile.EntryId).ToList();

        if (desired.SequenceEqual(current, StringComparer.Ordinal))
        {
            ForgetClosedSuppressions(desired);
            return;
        }

        ForgetClosedSuppressions(desired);
        RefreshTiles();
    }

    /// <summary>
    /// 清掉「已经不再运行」的移除记录，让那个应用下次再开时能重新出现。
    /// </summary>
    /// <remarks>
    /// 只在真的清掉了记录时才写盘：调和很频繁，每次都写文件既没必要，
    /// 也会让布局文件的修改时间不停地变。
    /// </remarks>
    private void ForgetClosedSuppressions(IReadOnlyCollection<string> stillRunning)
    {
        if (_services is null)
        {
            return;
        }

        var stillSuppressed = _services.Layouts.Dock.SuppressedAutoItemIds
            .Where(stillRunning.Contains)
            .ToList();

        if (stillSuppressed.Count == _services.Layouts.Dock.SuppressedAutoItemIds.Count)
        {
            return;
        }

        if (!_services.Layouts.RetainSuppressedAutoItems(stillSuppressed))
        {
            return;
        }

        try
        {
            _services.Layouts.SaveDock();
        }
        catch (StorageException)
        {
            // 写不进去只是让那个应用下次仍然不出现，不影响本次会话的界面；不改动界面。
        }
    }

    // ---- 通知与刷新 ----

    /// <summary>用当前的主题与材质状态更新说明文字。</summary>
    public void RefreshDiagnostics()
    {
        var theme = ThemeService.Current;

        var surface = SurfaceMode == GlassSurfaceMode.Acrylic
            ? "亚克力背景模糊"
            : "自绘半透明面板（无背景模糊）";

        StatusText.Text = $"{surface}｜{(theme?.IsDark == true ? "深色" : "浅色")}";
        StatusText.Visibility = Visibility.Collapsed;
    }

    /// <summary>
    /// 按生效中的程序坞设置刷新说明文字（D-16 的可见落点之一）。
    /// </summary>
    public void RefreshFromSettings()
    {
        StatusText.Visibility = Visibility.Collapsed;
        ApplyFrameLength();
        ApplyStripMetrics();
    }

    private void OnDockSettingsChanged(object? sender, EventArgs e)
    {
        RebuildAutoHide();
        RefreshFromSettings();
        RefreshTiles();

        // 位置与偏移也属于即时生效（D-16）：设置一变，已打开的 Dock 当场搬家。
        _slideOffsetPixels = 0;
        PositionOnDisplay();
    }

    /// <summary>请求宿主切换启动台；按钮点击附带按钮当前的物理屏幕矩形。</summary>
    private void RequestLauncherToggle(PixelRect? sourceBounds = null) => _toggleLauncher?.Invoke(sourceBounds);

    private void OnLauncherButtonClick(object sender, RoutedEventArgs e) => RequestLauncherToggle(GetLauncherButtonPixelBounds());

    /// <summary>Read the launcher button's current physical screen rectangle after Dock layout.</summary>
    public PixelRect? GetLauncherButtonPixelBounds()
    {
        if (!Settings.ShowLauncherEntry || LauncherButton.Visibility != Visibility.Visible
            || !IsVisible || !IsOpticalSurfaceVisible
            || LauncherButton.ActualWidth < 1 || LauncherButton.ActualHeight < 1) return null;

        try
        {
            var topLeft = LaunchpadTile.PointToScreen(new Point());
            var bottomRight = LaunchpadTile.PointToScreen(new Point(LaunchpadTile.ActualWidth, LaunchpadTile.ActualHeight));
            return new PixelRect((int)Math.Floor(topLeft.X), (int)Math.Floor(topLeft.Y),
                Math.Max(1, (int)Math.Ceiling(bottomRight.X - topLeft.X)),
                Math.Max(1, (int)Math.Ceiling(bottomRight.Y - topLeft.Y)));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private void OnThisPcButtonClick(object sender, RoutedEventArgs e)
    {
        var opened = ShellExecutor.Open("shell:MyComputerFolder", null, null);
        if (!opened.Started)
        {
            ShowFailureNotice("此电脑没能打开。");
        }
    }

    private void OnDesktopIconsButtonClick(object sender, RoutedEventArgs e) => ToggleDesktopIcons?.Invoke();

    private void OnSettingsButtonClick(object sender, RoutedEventArgs e) => OpenSettings?.Invoke();

    // ---- 摆放 ----

    /// <summary>
    /// 按生效中的设置把 Dock 摆到启动时绑定的主显示器（D-15/D-23）。
    /// </summary>
    /// <remarks>
    /// 摆放走<strong>启动时 Primary Monitor 的物理像素和 DPI</strong>；旧配置中的
    /// <c>TargetDisplayId</c> 只保留数据兼容，不参与生产窗口选择。矩形交给
    /// <see cref="DisplayService.MoveWindowToPixelRect"/>。
    /// 几何在 <see cref="DockPlacementCalculator"/>（纯函数，可离线单测）。
    /// </remarks>
    public bool SuppressPassiveTriggersWhenFullscreen { get; set; } = true;
    public void RefreshDisplayPlacement()
    {
        ApplyFrameLength();
        ApplyStripMetrics();
        PositionOnDisplay();
    }

    public void StopForPrimaryDisplayChange()
    {
        _frameTimer?.Stop();
        Hide();
    }

    public void ResumeAfterPrimaryDisplayChange(bool show)
    {
        RebuildAutoHide();
        RefreshDisplayPlacement();
        if (show && !_performanceSleepRequested) Show();
    }

    public void PositionOnDisplay()
    {
        var handle = Handle;

        if (handle == 0)
        {
            return;
        }

        var display = CurrentPrimaryDisplay;
        if (display is null)
        {
            return;
        }

        // 用了 SizeToContent，最终尺寸要等布局跑完才知道。
        var widthDips = ActualWidth > 0 ? ActualWidth : DesiredSize.Width;
        var heightDips = ActualHeight > 0 ? ActualHeight : DesiredSize.Height;

        if (widthDips <= 0 || heightDips <= 0)
        {
            return;
        }

        // 极端缩放下取整可能得 0；窗口至少要有 1 物理像素。
        var widthPixels = Math.Max(1, display.ToPixels(widthDips));
        var heightPixels = Math.Max(1, display.ToPixels(heightDips));

        _restingRect = DockPlacementCalculator.Calculate(
            display.WorkArea,
            Settings.Placement,
            Settings.OffsetX,
            Settings.OffsetY,
            widthPixels,
            heightPixels,
            display.Dpi);

        if (PrimaryDisplaySession is { IsAvailable: true } session)
            _restingRect = session.ClampWindowToWorkArea(_restingRect);

        _fullyHiddenOffsetPixels = SlideOutDistancePixels(_restingRect, display.Bounds, Settings.Placement);

        ApplySlide(_restingRect, _slideOffsetPixels);
    }

    /// <summary>保持 HWND 在主屏工作区内，只在窗口剪裁范围内滑动内容与共享面板几何。</summary>
    private void ApplySlide(PixelRect restingRect, double slideOffsetPixels)
    {
        var slidePixels = (int)Math.Round(slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var concealed = restingRect.Height > 0 && Math.Abs(slidePixels) >= restingRect.Height;
        // HWND equality describes layout, not the pixels painted while hidden.
        // Retire old glass only once the entire panel is clipped, so every reveal
        // prepares current content without disturbing the visible hide animation.
        if (concealed && !_panelWasConcealed) GlassSurface.RetireConcealedMaterial();
        _panelWasConcealed = concealed;
        var backdropActive = IsOpticalSurfaceVisible;
        if (backdropActive && !_backdropWasActive) GlassSurface.RequestBackdropRefresh(revealing: true);
        _backdropWasActive = backdropActive;
        if (PrimaryDisplaySession is { } session)
        {
            if (!session.IsAvailable) return;
            var fixedWindow = session.ClampWindowToWorkArea(restingRect);
            if (DisplayService.GetWindowPixelBounds(Handle) != fixedWindow)
                _ = DisplayService.MoveWindowToPixelRect(Handle, fixedWindow);
            var scale = Math.Max(.01, session.Display.ScaleFactor);
            var visualOffsetDip = slidePixels / scale;
            DockSlideTransform.Y = visualOffsetDip;
            _slidePanelBoundsDip = new Rect(0, visualOffsetDip, ActualWidth, ActualHeight);
            if (ActualWidth > 0 && ActualHeight > 0)
            {
                // Match the launcher: a fixed desktop texture with a moving GPU lens.
                // Only Content translates. New capture frames keep the same coordinates
                // throughout the slide and therefore remain consumable during motion.
                var hiddenOffsetDip = _fullyHiddenOffsetPixels * SlideOutDirection() / scale;
                GlassSurface.SetPanelShape(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
                GlassSurface.SetContainerTransitionBounds(
                    new Rect(0, Math.Min(0, hiddenOffsetDip), ActualWidth,
                        ActualHeight + Math.Abs(hiddenOffsetDip)));
                SetSharedPanelClip(_slidePanelBoundsDip.Value, PanelCornerRadius.TopLeft);
            }
            var shadowStrength = Math.Clamp(1 - Math.Abs(visualOffsetDip) / Math.Max(1, ActualHeight), 0, 1);
            SetTransitionShadowShape(Math.Abs(visualOffsetDip) < .01
                    ? null
                    : _slidePanelBoundsDip,
                PanelCornerRadius.TopLeft, shadowStrength);
            return;
        }

        // Unbound design/test windows keep their legacy placement behavior. AppHost binds
        // every production GlassWindow to one immutable PrimaryDisplaySession.
        DockSlideTransform.Y = 0;
        SetTransitionShadowShape(null, 0);
        var rect = restingRect with { Y = restingRect.Y + slidePixels };
        _ = DisplayService.MoveWindowToPixelRect(Handle, rect);
    }

    /// <summary>内容滑出方向：底部 Dock 向下 +1，顶部 Dock 向上 −1。</summary>
    private int SlideOutDirection()
        => Settings.Placement == DockPlacement.TopCenter ? -1 : 1;

    /// <summary>内容完全隐藏所需的物理像素偏移，窗口 HWND 本身始终留在主屏内。</summary>
    private static int SlideOutDistancePixels(PixelRect restingRect, PixelRect displayBounds, DockPlacement placement)
    {
        var distanceToDisplayEdge = placement == DockPlacement.TopCenter
            ? restingRect.Bottom - displayBounds.Y
            : displayBounds.Bottom - restingRect.Y;

        // Dock 靠工作区边缘停靠，工作区与显示器边缘之间可能有任务栏；再多移两像素，
        // 避免物理像素取整或窗口表面边界留下细线。
        return Math.Max(0, distanceToDisplayEdge) + 2;
    }

    // ---- 自动隐藏（D-19） ----

    /// <summary>按当前设置重建状态机。设置可随时改，状态机不可变所以整体换。</summary>
    private void RebuildAutoHide()
    {
        var settings = Settings;

        _autoHide = new AutoHideController(settings.AutoHideLeaveDelayMs);

        _autoHide.ForceShown(DateTime.UtcNow);
        _slideOffsetPixels = 0;

        if (_frameTimer is null)
        {
            // Capture completion/icon work can keep Normal queued while the panel
            // is concealed. Poll/slide admission must still advance to its first
            // visible pixel; settled idle remains 100 ms and game sleep stops it.
            _frameTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Send)
            {
                Interval = FrameInterval,
            };
            _frameTimer.Tick += (_, _) => OnAutoHideFrame();
        }

        _frameTimer.IsEnabled = !_performanceSleepRequested;
        _frameTimer.Interval = TimeSpan.FromMilliseconds(100);
    }

    /// <summary>
    /// 公共帧：探测指针 → 状态机 → 滑动插值。
    /// </summary>
    /// <remarks>
    /// 全部判定用<strong>缓存的静止矩形</strong>与显示枚举，不碰 WPF 可视树；
    /// 16 ms 一次的成本只有一次光标查询与少量算术。
    /// </remarks>
    private void OnAutoHideFrame()
    {
        if (_performanceSleepRequested) { AdvancePerformanceSleep(); return; }
        if (_autoHide is null || _services is null || _restingRect.Width == 0)
        {
            return;
        }

        var now = DateTime.UtcNow;
        var cursor = DisplayService.GetCursorPixelPoint();
        var fullscreen = FullscreenDetector.IsFullscreenActive();
        var covered = ForegroundCoverage.IsCovered(_restingRect);
        var suppressed = fullscreen || covered;
        var pointerOverDock = cursor is { } currentPoint
            && Inflate(GetVisibleDockRect(), 2).Contains(currentPoint.X, currentPoint.Y);

        var pointerOverDockPosition = cursor is { } position
            && Inflate(_restingRect, 2).Contains(position.X, position.Y);
        UpdateAutoHideState(now, fullscreen, covered, pointerOverDockPosition, pointerOverDock);

        // 滑动插值：向目标滑出量靠近。
        var target = _autoHide.State == AutoHideState.Hidden
            ? (double)_fullyHiddenOffsetPixels
            : 0.0;

        // Admit the reveal intent before preparation starts the native owner.
        // Waiting for optical visibility lets a foreground-policy tick put that
        // new owner back to sleep while the panel is still crossing the edge.
        NotifyPerformanceSurfaceVisibility();

        // Prepare a complete material before the first pixels slide back into view.
        if (target == 0 && _backgroundCaptureSleeping) SetBackgroundCaptureSleeping(false);
        else if (target == 0) PrepareConcealedReveal();

        if (ThemeService.Current?.AnimationsEnabled == false)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect,target);
            NotifyPerformanceSurfaceVisibility();
            return;
        }
        if (Math.Abs(_slideOffsetPixels - target) > 0.5)
        {
            _slideOffsetPixels += (target - _slideOffsetPixels) * 0.25;

            if (Math.Abs(_slideOffsetPixels - target) <= 0.5)
            {
                _slideOffsetPixels = target;
            }

            ApplySlide(_restingRect, _slideOffsetPixels);
        }
        else if (_slideOffsetPixels != target)
        {
            _slideOffsetPixels = target;
            ApplySlide(_restingRect, _slideOffsetPixels);
        }

        if (_frameTimer is not null)
            _frameTimer.Interval = !_backgroundCaptureSleeping && (suppressed || _autoHide.Suppressed)
                || Math.Abs(_slideOffsetPixels - target) > 0.5
                ? FrameInterval
                : TimeSpan.FromMilliseconds(100);
        NotifyPerformanceSurfaceVisibility();
    }

    private void UpdateAutoHideState(DateTime now, bool fullscreen, bool covered,
        bool pointerOverDockPosition, bool pointerOverVisibleDock)
    {
        if (_autoHide is null) return;
        if (_launcherContainerActive)
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }
        else if (fullscreen || covered)
        {
            if (_revealLocked)
            {
                _edgeRevealedDuringSuppression = false;
                _autoHide.ForceHide(now);
            }
            else if (_autoHide.State == AutoHideState.Hidden)
            {
                if (PassiveRevealPolicy.CanRevealDuringSuppression(pointerOverDockPosition, _revealLocked)
                    && _autoHide.TryReveal(false, now))
                    _edgeRevealedDuringSuppression = true;
            }
            else if (_edgeRevealedDuringSuppression)
            {
                if (pointerOverVisibleDock) _autoHide.PointerEnter(now);
                else _autoHide.PointerLeave(now);
                _autoHide.Poll(now);
            }
            else
                _autoHide.ForceHide(now);
        }
        else
        {
            _edgeRevealedDuringSuppression = false;
            _autoHide.ForceShown(now);
        }

        if (_autoHide.State == AutoHideState.Hidden)
            _edgeRevealedDuringSuppression = false;
    }

    private PixelRect GetVisibleDockRect()
    {
        var slidePixels = (int)Math.Round(_slideOffsetPixels * SlideOutDirection(), MidpointRounding.AwayFromZero);
        var translated = _restingRect with { Y = _restingRect.Y + slidePixels };
        if (PrimaryDisplaySession is null) return translated;
        var left = Math.Max(_restingRect.X, translated.X);
        var top = Math.Max(_restingRect.Y, translated.Y);
        var right = Math.Min(_restingRect.Right, translated.Right);
        var bottom = Math.Min(_restingRect.Bottom, translated.Bottom);
        return right <= left || bottom <= top
            ? new PixelRect(_restingRect.X, _restingRect.Y, 0, 0)
            : new PixelRect(left, top, right - left, bottom - top);
    }

    /// <summary>切换本次运行期间的遮挡唤出锁；不影响未被覆盖的 Dock。</summary>
    public void ToggleRevealLock()
    {
        _revealLocked = !_revealLocked;
        if (_performanceSleepRequested) return;
        if (_frameTimer is not null)
        {
            _frameTimer.Interval = FrameInterval;
            _frameTimer.IsEnabled = true;
        }
        OnAutoHideFrame();
    }

    private static PixelRect Inflate(PixelRect rect, int by)
        => new(rect.X - by, rect.Y - by, rect.Width + by * 2, rect.Height + by * 2);

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        RefreshDiagnostics();

        // 必须在句柄建立之后改扩展样式：点击本窗口时不得把前台让给别人。
        // 弹出物要取焦点时显式调 WindowActivation.TryBringToForeground。
        WindowStyles.MakeNoActivate(Handle);
    }

    protected override void OnClosed(EventArgs e)
    {
        _frameTimer?.Stop();
        _failureNoticeTimer?.Stop();
        if (_dockSettings is not null)
        {
            _dockSettings.Changed -= OnDockSettingsChanged;
        }

        if (_services is not null)
        {
            _services.Running.Changed -= OnRunningChanged;
        }

        base.OnClosed(e);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo sizeInfo)
    {
        base.OnRenderSizeChanged(sizeInfo);

        // 窗口尺寸（内容）变化后按设置重新落位；SizeToContent 的首次布局也会走到这里。
        PositionOnDisplay();
    }
}
