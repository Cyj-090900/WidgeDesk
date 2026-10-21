using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using GlassDesk.AppServices;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;

namespace GlassDesk.Launcher;

/// <summary>
/// 启动台里的「系统范围搜索」：把已安装应用与索引文件混进同一份结果。
/// </summary>
/// <remarks>
/// 这类结果<strong>不在应用库中</strong>，所以它们的槽位 id 带一个前缀
/// （<see cref="SystemTilePrefix"/>），渲染、点击与右键菜单据此分流。
/// 条目进入应用库有两条路：右键「添加到启动台」，或者直接打开它——打开一个应用
/// 就说明用户要用它，于是自动收进启动台（文件不收录，见 <c>LaunchSystemResult</c>）。
///
/// <para>
/// <b>查询必须防抖并放到后台。</b>索引查询是跨进程的，每敲一个字就同步查一次
/// 会让输入明显卡顿；同时结果要在后台线程取回、再回界面线程重建磁贴。
/// </para>
/// </remarks>
public partial class LauncherWindow
{
    /// <summary>系统结果槽位 id 的前缀。带它的 id 不是应用库 id。</summary>
    private const string SystemTilePrefix = "search:";

    /// <summary>停止输入多久之后才真正查询。太短会逐字查，太长会显得迟钝。</summary>
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(250);

    /// <summary>系统结果的总上限，防止一次搜索拉回成百上千条。</summary>
    private const int SystemResultLimit = 120;

    private DispatcherTimer? _systemSearchTimer;
    private long _systemSearchGeneration;
    private long _systemSearchRevision;
    private IReadOnlyList<SystemSearchResult> _systemResults = [];
    private Dictionary<string, SystemSearchResult> _systemTiles =
        new Dictionary<string, SystemSearchResult>(StringComparer.Ordinal);

    /// <summary>当前是否应当把系统结果并入搜索。</summary>
    private bool SystemSearchEnabled => _settings.SearchInstalledApps || _settings.SearchFiles;

    /// <summary>系统结果的条数，供诊断面板显示。</summary>
    private int SystemResultCount => _systemResults.Count;

    /// <summary>查询词变化后重排一次查询（防抖）。</summary>
    private void ScheduleSystemSearch()
    {
        _systemSearchTimer?.Stop();
        _systemSearchTimer = null;

        if (_services is null || !_view.IsSearching || !SystemSearchEnabled)
        {
            // 清空搜索或系统结果被关掉：立即丢掉旧结果，避免它们残留在网格里。
            if (_systemResults.Count > 0)
            {
                _systemResults = [];
                _systemTiles = new Dictionary<string, SystemSearchResult>(StringComparer.Ordinal);
                _systemSearchRevision++;
            }

            return;
        }

        var timer = new DispatcherTimer(DispatcherPriority.Background, Dispatcher) { Interval = SearchDebounce };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (!ReferenceEquals(_systemSearchTimer, timer)) return;
            _systemSearchTimer = null;
            StartSystemSearch(_view.Query);
        };
        _systemSearchTimer = timer;
        timer.Start();
    }

    /// <summary>在后台线程执行一次系统搜索，回来后重建磁贴。</summary>
    private void StartSystemSearch(string query)
    {
        if (_services is null) return;

        var generation = ++_systemSearchGeneration;
        var includeApps = _settings.SearchInstalledApps;
        var includeFiles = _settings.SearchFiles;
        var search = _services.SystemSearch;

        _ = Task.Run(() =>
        {
            IReadOnlyList<SystemSearchResult> results;

            try
            {
                results = search.Search(query, SystemResultLimit, includeApps, includeFiles);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 系统搜索是增强功能：出问题就当这次没有结果，绝不打扰用户。
                _ = ex;
                results = [];
            }

            return results;
        }).ContinueWith(task =>
        {
            if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished) return;

            _ = Dispatcher.BeginInvoke(() =>
            {
                // 期间又敲了字：这一批结果已经过期，直接丢掉。
                if (generation != _systemSearchGeneration) return;

                _systemResults = task.Result;
                _systemTiles = BuildSystemTileIndex(_systemResults);
                _systemSearchRevision++;
                RenderTiles();
            });
        }, TaskScheduler.Default);
    }

    private static Dictionary<string, SystemSearchResult> BuildSystemTileIndex(
        IReadOnlyList<SystemSearchResult> results)
    {
        var index = new Dictionary<string, SystemSearchResult>(StringComparer.Ordinal);

        for (var i = 0; i < results.Count; i++)
        {
            // 用序号而不是目标做键：目标里有反斜杠、冒号，还可能很长；
            // 序号在「一批结果」的生存期内是稳定的，而每次新结果都会整体换掉这张表。
            index[SystemTilePrefix + i.ToString(System.Globalization.CultureInfo.InvariantCulture)] = results[i];
        }

        return index;
    }

    /// <summary>这个槽位 id 是否是一条系统搜索结果。</summary>
    private static bool IsSystemTileId(string? id)
        => id is not null && id.StartsWith(SystemTilePrefix, StringComparison.Ordinal);

    private bool TryGetSystemResult(string? id, out SystemSearchResult? result)
    {
        result = null;
        return id is not null && _systemTiles.TryGetValue(id, out result);
    }

    /// <summary>
    /// 取一条系统结果的图标。
    /// </summary>
    /// <remarks>
    /// 与库内条目走同一个加载器（同一个后台 STA、同一份缓存预算），区别只在缓存键：
    /// 系统条目没有应用库版本号可依附，改用「结果内容 + 目标」做键，
    /// 这样同名同目标的项复用一张图，不同项不会互相顶掉。
    /// </remarks>
    private void RequestSystemIcon(SystemSearchResult result, System.Windows.Controls.Border host, double size)
    {
        var icons = _services?.Icons;
        if (icons is null) return;

        var pixels = IconPixelSize(size);
        var generation = _systemSearchRevision;
        var key = $"system\0{result.Target}\0{pixels}";
        var task = _iconLoader.LoadAsync(key, () => LoadSystemIcon(icons, result.IconSource, pixels));

        if (task.IsCompletedSuccessfully) Apply(task.Result);
        else _ = CompleteAsync();

        void Apply(System.Windows.Media.Imaging.BitmapImage? image)
        {
            // 结果集已经换代（用户又敲了字）时不要再把旧图填进来。
            if (image is null || generation != _systemSearchRevision) return;
            if (_backgroundWorkSuspended || pixels != IconPixelSize(size)) return;
            host.Child = new System.Windows.Controls.Image
            {
                Source = image, Width = size, Height = size,
                Stretch = System.Windows.Media.Stretch.Uniform,
                HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            };
        }

        async Task CompleteAsync()
        {
            var image = await task.ConfigureAwait(false);
            if (image is null || host.Dispatcher.HasShutdownStarted) return;
            try
            {
                await host.Dispatcher.InvokeAsync(() => Apply(image),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (TaskCanceledException) { }
        }
    }

    private static System.Windows.Media.Imaging.BitmapImage? LoadSystemIcon(
        GlassDesk.AppServices.IconCache icons, string iconSource, int pixels)
    {
        if (string.IsNullOrWhiteSpace(iconSource)) return null;

        // EnsureFor 内部就是「命中缓存就复用、否则提取并落盘」，所以直接用它即可。
        var path = icons.EnsureFor(iconSource)?.Path;
        if (string.IsNullOrWhiteSpace(path) || !System.IO.File.Exists(path)) return null;

        try
        {
            return LauncherIconDecoder.DecodeFile(path, pixels);
        }
        catch (Exception ex) when (ex is System.IO.IOException or UriFormatException or System.IO.FileFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// 打开一条系统结果。
    /// </summary>
    /// <remarks>
    /// 走外壳同一条路：应用是 <c>shell:AppsFolder\&lt;AUMID&gt;</c>、
    /// 文件是完整路径，两者 <c>ShellExecute</c> 都认。
    ///
    /// <para>
    /// <b>应用打开后自动收进启动台。</b>用户特意搜到它、又点开它，这就是「我常用它」的
    /// 意思；下次他想再打开时，不必重新搜一遍。<strong>文件不收录</strong>——文件多半是
    /// 一次性查看，把每个打开过的文件都塞进启动台只会把它撑满。
    /// </para>
    ///
    /// <para>
    /// 顺序是<b>先启动、后入库</b>。用户的意图是打开它，那个动作不该被任何写入动作挡在
    /// 后面：入库失败最多是少一条记录，不该让程序打不开。
    /// </para>
    /// </remarks>
    private void LaunchSystemResult(SystemSearchResult result)
    {
        if (string.IsNullOrWhiteSpace(result.Target)) return;

        var outcome = GlassDesk.Platform.Shell.ShellExecutor.Open(result.Target);

        if (!outcome.Started)
        {
            ShowNotice(outcome.IsFileNotFound
            ? $"目标已不在原处：{result.DisplayName}"
            : outcome.IsNoAssociation
                ? $"不知道用什么程序打开：{result.DisplayName}"
                : $"{result.DisplayName} 未能打开。");
            return;
        }

        AddOpenedResultToLauncher(result);
        CloseWithTransition();
    }

    /// <summary>
    /// 把刚打开的应用收进启动台。文件会被忽略（见下）。
    /// </summary>
    /// <remarks>
    /// <b>只收应用。</b>文件多半是一次性查看，把每个打开过的文件都塞进启动台只会把它撑满。
    /// 判断放在这里而不是调用方，是为了让这个方法自己就是安全的——将来多一个调用点，
    /// 也不会因为忘了判断而把文件收进去。
    ///
    /// <para>
    /// 写入失败<strong>不打断启动</strong>：这里只记录提示，异常不往上抛。窗口马上就要
    /// 收起了，弹一个错误框去质问用户「要不要重试」既不合适、也没法处理。
    /// </para>
    ///
    /// <para>
    /// 这里刻意<strong>不</strong>重建磁贴：<see cref="ImportService.AddInstalledApp"/> 已经把
    /// 新条目写进库与布局并落盘，而窗口正在收起，重建出来的磁贴一帧也不会被看到。
    /// 下次打开启动台时 PrepareToShow 会重新渲染。
    /// </para>
    /// </remarks>
    private void AddOpenedResultToLauncher(SystemSearchResult result)
    {
        if (_services is null || result.Kind != SystemSearchKind.App) return;

        try
        {
            var entry = AppEntryFactory.CreateFromInstalledApp(
                result.DisplayName, result.Target, result.AppUserModelId, result.IconSource);
            _services.Import.AddInstalledApp(entry);
        }
        catch (Exception ex) when (ex is StorageException or IOException or UnauthorizedAccessException)
        {
            Trace.WriteLine($"Launcher result was not added to the library: {ex.Message}");
        }
    }

    /// <summary>把一条系统结果加入应用库并放进启动台。</summary>
    private void AddSystemResultToLauncher(SystemSearchResult result)
    {
        if (_services is null) return;

        try
        {
            var before = LauncherLayoutRules.AppIds(_workspace?.Layout ?? new LauncherLayout())
                .ToHashSet(StringComparer.Ordinal);

            var entry = AppEntryFactory.CreateFromInstalledApp(
                result.DisplayName, result.Target, result.AppUserModelId, result.IconSource);

            var outcome = _services.Import.AddInstalledApp(entry);

            var placed = _workspace is not null
                && LauncherLayoutRules.AppIds(_workspace.Layout).Contains(entry.Id);

            ShowNotice(outcome switch
            {
                AddEntryResult.Added when placed && !before.Contains(entry.Id) => "已添加到启动台。",
                AddEntryResult.Added => "已添加到启动台。",
                AddEntryResult.Duplicate => "这一项已经在启动台中了。",
                _ => "这一项无法添加。",
            }, autoDismiss: outcome == AddEntryResult.Added);

            RefreshLibrary();
        }
        catch (StorageException ex)
        {
            ShowNotice("应用库写入失败；请检查数据目录并重试。" + ex.Message);
        }
    }
}
