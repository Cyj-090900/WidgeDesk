using System.ComponentModel;
using System.Windows;

namespace GlassDesk.Dock;

/// <summary>
/// 一枚 Dock 磁贴的显示数据。
/// </summary>
/// <remarks>
/// 刻意做成了最小视图模型：图标与尺寸在构建时确定（设置变更时整行重建），
/// 而<strong>运行状态点</strong>用属性通知单独刷新——调和事件只改圆点，
/// 不重建磁贴，避免每次窗口生灭都重载一遍图标。
/// </remarks>
public sealed class DockTileModel : INotifyPropertyChanged
{
    private Visibility _runningVisibility = Visibility.Collapsed;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>应用库条目 id。点击时据此走启动/激活。</summary>
    public string EntryId { get; init; } = string.Empty;

    /// <summary>
    /// 这是一枚「自动」磁贴（此刻正在运行、用户没固定过它）。
    /// </summary>
    /// <remarks>
    /// 自动磁贴与固定磁贴的差别有二：应用关掉后它会自己消失，并且右键菜单里有
    /// 「退出程序」。固定磁贴两者都不适用——用户固定它就是要它一直在。
    /// </remarks>
    public bool IsAuto { get; init; }

    /// <summary>
    /// 点击时交给启动服务的 id。
    /// </summary>
    /// <remarks>
    /// 自动磁贴若对应库里已有的条目，这里就是那个条目 id，于是走「激活已有窗口」——
    /// 单实例应用会把自己提到前台，而不是再开一个。库里没有对应条目时为 null，
    /// 点击改为按目标启动。
    /// </remarks>
    public string? LaunchEntryId { get; init; }

    /// <summary>自动磁贴的启动目标；固定磁贴为 null（用 <see cref="EntryId"/> 即可）。</summary>
    public string? LaunchTarget { get; init; }

    /// <summary>自动磁贴身份；没有为 null。</summary>
    public string? Aumid { get; init; }

    /// <summary>自动磁贴图标来源。固定磁贴为 null——它的图标由应用库条目决定。</summary>
    public string? AutoIconSource { get; init; }

    /// <summary>自动磁贴对应的库条目自选图标；没有为 null。</summary>
    public string? AutoCustomIconPath { get; init; }

    /// <summary>
    /// 属于这个应用的全部候选窗口，供「退出程序」逐一投递关闭。
    /// </summary>
    /// <remarks>
    /// 记下窗口句柄而不是进程 id：一个进程可能有多个窗口，逐窗口关闭走的是各自的
    /// 关闭流程（能触发「是否保存」这类询问），而按进程结束会丢掉用户的未保存内容。
    /// </remarks>
    public IReadOnlyList<nint> Windows { get; init; } = [];

    /// <summary>内置入口的符号字形（Segoe Fluent Icons）。普通磁贴为 null（用图标）。</summary>
    public string? Glyph { get; init; }

    /// <summary>目标已失效（R-07）。失效磁贴的图标降透明显示。</summary>
    public bool IsMissing { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    /// <summary>悬停提示（D-06）：完整名称，不截断；失效条目附说明。</summary>
    public string ToolTipText { get; init; } = string.Empty;

    public System.Windows.Media.ImageSource? Icon { get; init; }

    /// <summary>运行状态点的颜色。来自当前调色板的前景色。</summary>
    public System.Windows.Media.Brush? DotBrush { get; init; }

    /// <summary>图标边长，DIP，来自程序坞设置。</summary>
    public double IconSize { get; init; } = 48;

    /// <summary>磁贴外边距，由图标间距设置推导。</summary>
    public Thickness TileMargin { get; init; } = new(4, 0, 4, 0);

    /// <summary>运行状态点（D-07）的可见性。调和事件单独刷新它。</summary>
    public Visibility RunningVisibility
    {
        get => _runningVisibility;
        set
        {
            if (_runningVisibility == value)
            {
                return;
            }

            _runningVisibility = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RunningVisibility)));
        }
    }
}
