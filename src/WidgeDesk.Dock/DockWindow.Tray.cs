using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.Platform.Shell;
using WidgeDesk.UI.Theming;

namespace WidgeDesk.Dock;

/// <summary>
/// 右侧托盘区：网络、音量、日期时间。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么不复制系统浮层。</b>实测这台 Windows 10 上，任务栏的
/// <c>TrayButton</c>（操作中心）与 <c>TrayClockWClass</c>（时钟）对
/// <c>WM_LBUTTONDOWN</c> 合成消息没有任何反应；换成真实的
/// <c>SetCursorPos</c> + <c>mouse_event</c> 点击，两个也都不出浮层，
/// 只有托盘通知区冒出一个输入法状态窗口。系统浮层不接受这种输入，
/// 因此"把浮层搬到 Dock 上"这条路在本机走不通。
/// </para>
/// <para>
/// <b>所以做什么。</b>Dock 自己画这三个图标，状态来自真实系统：
/// 网络形态取自 <see cref="NetworkInterface"/> 的活动适配器，
/// 音量取自 Core Audio 的默认播放设备。点击打开的是系统本身能到达的去处
/// （网络设置、音量合成器、日期时间设置），不是我们伪造的面板。
/// </para>
/// <para>
/// <b>失败一律降级。</b>取不到音频设备就画无声图标并禁用点击，
/// 网络服务异常就画离线图标。托盘绝不能让 Dock 启动失败。
/// </para>
/// </remarks>
public partial class DockWindow
{
    private System.Windows.Threading.DispatcherTimer? _trayTimer;
    private bool _trayAvailable = true;

    /// <summary>隐藏托盘区（从设置里关掉时）。</summary>
    public bool IsTrayVisible => TrayArea.Visibility == Visibility.Visible;

    /// <summary>供测试与诊断读取当前音量；没有可用设备时为 <c>null</c>。</summary>
    public VolumeStatus? CurrentVolume { get; private set; }

    /// <summary>供测试与诊断读取当前网络状态。</summary>
    public NetworkStatus CurrentNetwork { get; private set; } = new(NetworkKind.Offline, null, false);

    private void InitializeTray()
    {
        RefreshTray();
        // 一分钟足够：托盘显示的是时间与连接形态，不是实时计数器。
        // 秒级刷新会让 Dock 每秒重绘一次，和"闲置时应尽量不动"冲突。
        _trayTimer = new System.Windows.Threading.DispatcherTimer(System.Windows.Threading.DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromSeconds(15),
        };
        _trayTimer.Tick += (_, _) => RefreshTray();
        IsVisibleChanged += (_, e) => { if (e.NewValue is true) { RefreshTray(); _trayTimer?.Start(); } else _trayTimer?.Stop(); };
        Closed += (_, _) => _trayTimer?.Stop();
    }

    /// <summary>重新采样系统状态并刷新三个图标。</summary>
    public void RefreshTray()
    {
        if (!_trayAvailable) return;
        try
        {
            RefreshNetwork();
            RefreshVolume();
            RefreshClock();
        }
        catch (Exception error) when (error is InvalidOperationException or ExternalException or IOException)
        {
            // 托盘是装饰性的：任何一项失败都不该影响 Dock 本体。
            _trayAvailable = false;
        }
    }

    private void RefreshNetwork()
    {
        CurrentNetwork = TrayStatusReader.ReadNetwork();
        NetworkGlyph.Data = Geometry.Parse(CurrentNetwork.Kind switch
        {
            // 三段弧形信号格：与系统 Wi-Fi 图标同一语义，不依赖任何图标字体。
            NetworkKind.Wireless => "M3.2,9.6 A13,13 0 0 1 20.8,9.6 M6.4,13.4 A8.4,8.4 0 0 1 17.6,13.4 M9.8,17 A3.4,3.4 0 0 1 14.2,17",
            NetworkKind.Wired => "M3,8 H21 V15 H3 Z M6,15 V19 M18,15 V19",
            _ => "M4,4 L20,20 M4,20 L20,4",
        });
        NetworkDot.Visibility = CurrentNetwork.Kind == NetworkKind.Offline ? Visibility.Collapsed : Visibility.Visible;
        NetworkButton.ToolTip = CurrentNetwork.Kind switch
        {
            NetworkKind.Wireless => $"无线网络：{CurrentNetwork.Name}",
            NetworkKind.Wired => $"有线网络：{CurrentNetwork.Name}",
            _ => "未连接网络",
        };
        NetworkButton.ToolTip = $"{NetworkButton.ToolTip}{Environment.NewLine}点按打开网络设置";
    }

    private void RefreshVolume()
    {
        CurrentVolume = TrayStatusReader.ReadVolume();
        if (CurrentVolume is null)
        {
            VolumeButton.IsEnabled = false;
            VolumeButton.ToolTip = "没有可用的播放设备";
            VolumeWave.Visibility = Visibility.Collapsed;
            VolumeMutedCross.Visibility = Visibility.Visible;
            return;
        }

        VolumeButton.IsEnabled = true;
        var muted = CurrentVolume.Muted || CurrentVolume.Level <= 0.001;
        VolumeMutedCross.Visibility = muted ? Visibility.Visible : Visibility.Collapsed;
        VolumeWave.Visibility = muted ? Visibility.Collapsed : Visibility.Visible;
        VolumeButton.ToolTip = muted
            ? $"已静音（{CurrentVolume.Level:P0}）{Environment.NewLine}滚轮调音量，右键切换静音"
            : $"音量 {CurrentVolume.Level:P0}{Environment.NewLine}滚轮调音量，右键切换静音";
    }

    private void RefreshClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("HH:mm", CultureInfo.CurrentCulture);
        ClockDate.Text = now.ToString("yyyy/M/d", CultureInfo.CurrentCulture);
        ClockButton.ToolTip = now.ToString("yyyy年M月d日 dddd HH:mm", CultureInfo.CurrentCulture)
            + Environment.NewLine + "点按打开日期与时间设置";
    }

    /// <summary>点击网络图标：优先唤出真正的操作中心（与任务栏那颗按钮同一目的）。</summary>
    private void OnNetworkClick(object sender, RoutedEventArgs e)
    {
        if (ActionCenter.Toggle()) return;
        // 系统不认这条路径时退到网络设置：那是确定能到达的地方，而不是点不动的图标。
        ShellExecutor.Open("ms-settings:network-status");
    }

    private void OnVolumeClick(object sender, RoutedEventArgs e) => TrayStatusReader.OpenVolumeMixer();

    /// <summary>点击时钟：同样是操作中心（Win10 的日历与通知都在那里）。</summary>
    private void OnClockClick(object sender, RoutedEventArgs e)
    {
        if (ActionCenter.Toggle()) return;
        ShellExecutor.Open("ms-settings:dateandtime");
    }

    /// <summary>滚轮调音量；右键切换静音。</summary>
    private void OnVolumeWheel(object sender, MouseWheelEventArgs e)
    {
        if (CurrentVolume is null) return;
        var step = e.Delta > 0 ? .05 : -.05;
        if (TrayStatusReader.SetVolume(CurrentVolume.Level + step)) RefreshVolume();
        e.Handled = true;
    }

    private void OnVolumeRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (CurrentVolume is null) return;
        if (TrayStatusReader.SetMuted(!CurrentVolume.Muted)) RefreshVolume();
        e.Handled = true;
    }

    /// <summary>
    /// 托盘区占用的宽度，DIP；用于外框与磁贴条的可用宽度计算。
    /// </summary>
    /// <remarks>
    /// 这里按同一套尺寸常量算，而不是读 <see cref="FrameworkElement.ActualWidth"/>：
    /// 首次布局前它还是 0，会把磁贴条的上限算大一点，而之后没有时机再修正。
    /// 托盘宽度本身与磁贴条无关，用常量既准确又不会引入布局回合。
    /// </remarks>
    private double TrayWidthDip => TrayArea.Visibility != Visibility.Visible
        ? 0
        : (30 + 30 + 58 + 8 + 6 + 1) * DockScale;
}
