namespace WidgeDesk.Core.Models;

/// <summary>
/// Dock 排列。与应用库分开存储，这样重排
/// Dock 绝不会损坏 <see cref="AppEntry"/>。
/// </summary>
public sealed class DockLayout
{
    /// <summary>
    /// Dock 中显示的应用库条目 id，按顺序排列。库中已不存在的
    /// id 属于过期项，应当跳过而不是当作错误——用户可能已经
    /// 删掉了该应用，Dock 只需相应缩短。
    /// </summary>
    public List<string> PinnedItemIds { get; set; } = [];

    /// <summary>
    /// 用户明确从 Dock 上移除过的自动项。
    /// </summary>
    /// <remarks>
    /// 没有这份记录，「从 Dock 移除」对一个正在运行的应用就是无效操作——
    /// 下一次调和发现它还在运行，会立刻把图标加回来，用户看到的是菜单点了没反应。
    /// 应用真的关掉之后这条记录会被清掉，下次再运行时可以重新出现。
    ///
    /// <para>
    /// 只记「用户移除过什么」，不记「有哪些自动项」：后者由当前运行的窗口推导，
    /// 是运行期状态。把它写进文件既没有意义（重启后应当以真实运行情况为准），
    /// 又会让布局文件里出现一份会过期的快照。
    /// </para>
    /// </remarks>
    public List<string> SuppressedAutoItemIds { get; set; } = [];
}
