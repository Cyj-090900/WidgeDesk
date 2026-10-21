namespace WidgeDesktop.Core.Models.Settings;

/// <summary>启动台的网格、窗口与搜索行为。</summary>
public sealed class LauncherSettings
{
    /// <summary>每页的网格行数。</summary>
    public int Rows { get; set; } = 5;

    /// <summary>每页的网格列数。</summary>
    public int Columns { get; set; } = 7;

    /// <summary>
    /// 窗口模式下占工作区的比例。计划书要求大约
    /// 三分之二，居中。
    /// </summary>
    public double WindowWorkAreaRatio { get; set; } = 0.66;

    /// <summary>铺满整个工作区，而不是显示居中的面板。</summary>
    public bool FullscreenMode { get; set; }

    /// <summary>
    /// 启动台一打开就把光标放进搜索框，键入即过滤，
    /// 不必再多点一下。
    /// </summary>
    public bool FocusSearchOnOpen { get; set; } = true;

    /// <summary>
    /// 再次打开时恢复上次停留的页面，关闭则总是从第一页开始。
    /// </summary>
    public bool RememberLastPage { get; set; } = true;

    /// <summary>
    /// 搜索时一并查本机已安装的应用（含打包应用与经典程序）。
    /// </summary>
    /// <remarks>
    /// 关闭后搜索只覆盖应用库里的条目，也就是这个功能上线之前的行为。
    /// 默认开启：用户按下搜索框时的预期是「找机器上任何东西」。
    /// </remarks>
    public bool SearchInstalledApps { get; set; } = true;

    /// <summary>
    /// 搜索时一并查 Windows 搜索索引里的本地文件。
    /// </summary>
    /// <remarks>
    /// 与 <see cref="SearchInstalledApps"/> 分别开关：查文件比查应用慢，
    /// 有些用户只想要一个启动器。
    /// </remarks>
    public bool SearchFiles { get; set; } = true;
}
