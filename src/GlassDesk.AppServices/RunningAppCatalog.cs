using System.Diagnostics;
using GlassDesk.Core.Models;
using GlassDesk.Core.Storage;
using GlassDesk.Platform.Shell;

namespace GlassDesk.AppServices;

/// <summary>
/// 一个此刻正在运行、可以放到 Dock 上的应用。
/// </summary>
/// <param name="Id">
/// 稳定的槽位 id，形如 <c>auto:&lt;目标&gt;</c>。同一次运行内重复调和得到同一个值，
/// 磁贴的身份因此稳定；应用关掉后这个 id 自然消失。
/// </param>
/// <param name="DisplayName">用户可见的名称。</param>
/// <param name="Target">启动目标：应用库条目用它的目标，否则是 <c>shell:AppsFolder\&lt;AUMID&gt;</c> 或映像路径。</param>
/// <param name="Aumid">应用身份；没有为 null。</param>
/// <param name="IconSource">图标来源，直接交给图标缓存。</param>
/// <param name="CustomIconPath">
/// 用户在库条目上自选过的图标文件；没有为 null。
/// </param>
/// <param name="EntryId">
/// 对应的应用库条目 id；库里没有这一条时为 null。有值时点击走「激活该条目」，
/// 单实例应用会把自己已有窗口提到前面，而不是再开一个。
/// </param>
/// <param name="Windows">此刻属于这个应用的全部候选顶层窗口，供「退出程序」使用。</param>
/// <param name="WindowTitles">
/// 与 <paramref name="Windows"/> 一一对应的窗口标题，供多窗口选择列表使用。
/// 与任务栏一致：点一个有多个窗口的应用时先让用户选是哪个。
/// </param>
public sealed record RunningApp(
    string Id,
    string DisplayName,
    string Target,
    string? Aumid,
    string IconSource,
    string? CustomIconPath,
    string? EntryId,
    IReadOnlyList<nint> Windows,
    IReadOnlyList<string> WindowTitles);

/// <summary>
/// 把一次窗口快照归纳成「哪些应用正在运行」。
/// </summary>
/// <remarks>
/// <b>纯函数。</b>输入是窗口快照与应用库，输出是自动磁贴的原料；不碰窗口、不落盘、
/// 不依赖进程状态，因此可以在没有窗口的环境里单测。真正的枚举由
/// <see cref="RunningAppTracker"/> 负责。
///
/// <para>
/// <b>为什么单独一层，而不是塞进 <see cref="RunningAppTracker"/>。</b>
/// 后者的职责是「谁在运行」——它只回答是/否，供状态点使用。这里要回答的是
/// 「把它画成磁贴需要什么」：名称、图标、启动目标、以及能关掉哪些窗口。
/// 两者的输入相同、用途不同，混在一起会让那个类同时承担状态与呈现两种职责。
/// </para>
/// </remarks>
public static class RunningAppCatalog
{
    /// <summary>自动磁贴槽位 id 的前缀。带它的 id 不是应用库 id。</summary>
    public const string AutoTilePrefix = "auto:";

    /// <summary>这个 id 是不是自动磁贴。</summary>
    public static bool IsAutoTile(string? itemId)
        => itemId is not null && itemId.StartsWith(AutoTilePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 点击一枚自动磁贴时该做什么。
    /// </summary>
    /// <remarks>
    /// 抽成纯枚举而不是把 if 写在窗口里，是因为「绝不能重复启动」这条规则
    /// 要能被测试直接断言——它正是「点一次多开一个」那个缺陷的落点。
    /// </remarks>
    public enum Activation
    {
        /// <summary>只有一个窗口：把它提到前面。</summary>
        Raise = 0,

        /// <summary>有多个窗口：先让用户选是哪一个（与任务栏点分组图标一致）。</summary>
        Choose = 1,

        /// <summary>一个可用窗口都没有：这才轮到启动。</summary>
        Launch = 2,
    }

    /// <summary>
    /// 给定「还剩几个可用窗口」，决定点击该做什么。
    /// </summary>
    /// <remarks>
    /// <b>只要还有窗口，就绝不返回 <see cref="Activation.Launch"/>。</b>
    /// 自动磁贴正是因为「这个应用有窗口」才存在，点它时再去启动，等于要求应用
    /// 自己判断「我已经开着了吗」——多数程序不做这个判断，于是多开一个。
    /// </remarks>
    public static Activation DecideActivation(int availableWindows)
        => availableWindows switch
        {
            <= 0 => Activation.Launch,
            1 => Activation.Raise,
            _ => Activation.Choose,
        };

    /// <summary>
    /// 归纳出此刻正在运行、且还没有被固定到 Dock 的应用。
    /// </summary>
    /// <param name="windows">窗口快照，通常来自 <see cref="WindowLocator.EnumerateTopLevelWindows"/>。</param>
    /// <param name="library">应用库，用来认领已有条目（拿到用户改过的名称与自定义图标）。</param>
    /// <param name="coveredEntryIds">
    /// 已经有磁贴在 Dock 上的条目 id——通常是固定项。它们不再生成自动磁贴，
    /// 否则同一个应用会出现两枚图标。
    /// </param>
    /// <param name="suppressedAutoIds">
    /// 用户明确从 Dock 上移除过的自动项 id，本次运行内不再出现。
    /// </param>
    /// <param name="ownProcessId">本进程 id；它自己的窗口（Dock、启动台）不算「运行中的应用」。</param>
    public static IReadOnlyList<RunningApp> Build(
        IReadOnlyList<WindowIdentityInfo> windows,
        AppLibraryStore library,
        IReadOnlyCollection<string> coveredEntryIds,
        IReadOnlyCollection<string> suppressedAutoIds,
        uint ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(windows);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(coveredEntryIds);
        ArgumentNullException.ThrowIfNull(suppressedAutoIds);

        var covered = coveredEntryIds as ISet<string> ?? coveredEntryIds.ToHashSet(StringComparer.Ordinal);
        var suppressed = suppressedAutoIds as ISet<string> ?? suppressedAutoIds.ToHashSet(StringComparer.Ordinal);

        // 先按「应用」归组。分组键优先用身份：两个 UWP 应用共享同一个宿主进程，
        // 按映像路径分组会把它们并成一个。
        var groups = new Dictionary<string, Group>(StringComparer.OrdinalIgnoreCase);
        var order = new List<string>();

        foreach (var window in windows)
        {
            if (window.ProcessId == ownProcessId)
            {
                continue;
            }

            var key = GroupKey(window);

            if (key is null)
            {
                // 既没有身份也读不到映像路径：无从判断它是谁，也就无从启动或关闭它。
                continue;
            }

            if (!groups.TryGetValue(key, out var group))
            {
                group = new Group(window);
                groups[key] = group;
                order.Add(key);
            }

            group.Windows.Add(window);
        }

        var result = new List<RunningApp>();

        foreach (var key in order)
        {
            var group = groups[key];
            var entry = FindEntry(library, group);

            // 已经在 Dock 上（固定项）的条目不再生成自动磁贴。
            if (entry is not null && covered.Contains(entry.Id))
            {
                continue;
            }

            var target = entry?.Target ?? FallbackTarget(group);
            var id = AutoTilePrefix + target.ToLowerInvariant();

            if (suppressed.Contains(id))
            {
                continue;
            }

            result.Add(new RunningApp(
                id,
                entry?.DisplayName ?? FallbackName(group),
                target,
                group.Aumid,
                entry?.IconSource ?? group.IconSource,
                entry?.CustomIconPath,
                entry?.Id,
                [.. group.Windows.Select(window => window.Handle)],
                [.. group.Windows.Select(window => window.Title)]));
        }

        return result;
    }

    /// <summary>分组键：身份优先，其次映像路径。都没有时返回 null。</summary>
    private static string? GroupKey(WindowIdentityInfo window)
    {
        if (!string.IsNullOrWhiteSpace(window.EffectiveAumid))
        {
            return "aumid:" + window.EffectiveAumid;
        }

        return string.IsNullOrWhiteSpace(window.ImagePath) ? null : "image:" + window.ImagePath;
    }

    /// <summary>在这次运行的窗口里挑一个代表，用来取名称与图标。</summary>
    private static AppEntry? FindEntry(AppLibraryStore library, Group group)
    {
        if (!string.IsNullOrWhiteSpace(group.Aumid))
        {
            var byAumid = library.Items.FirstOrDefault(item => item is { IsUsable: true }
                && string.Equals(item.AppUserModelId, group.Aumid, StringComparison.OrdinalIgnoreCase));

            if (byAumid is not null)
            {
                return byAumid;
            }
        }

        if (string.IsNullOrWhiteSpace(group.ImagePath))
        {
            return null;
        }

        // 库里可能有多条指向同一个程序的记录（例如同名的 .lnk 与 .exe），
        // 取第一条即可——它们指向同一个可执行文件，名称与图标都一样。
        return library.Items.FirstOrDefault(item => item is { IsUsable: true }
            && string.Equals(item.Target, group.ImagePath, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>库里没有这条记录时用什么目标启动它。</summary>
    private static string FallbackTarget(Group group)
        => string.IsNullOrWhiteSpace(group.Aumid) ? group.ImagePath! : $@"shell:AppsFolder\{group.Aumid}";

    /// <summary>
    /// 库里没有这条记录时的显示名称。
    /// </summary>
    /// <remarks>
    /// 用映像文件的「文件说明」（<c>FileDescription</c>）——它在资源管理器里
    /// 显示的就是这个，通常还是本地化的（<c>Notepad</c> / <c>记事本</c>）。
    /// 读不到时退回文件名。刻意不用窗口标题：标题多半是「文档.txt - 记事本」，
    /// 把它当作应用名既长又不稳定。
    /// </remarks>
    private static string FallbackName(Group group)
    {
        var path = group.ImagePath;

        if (!string.IsNullOrWhiteSpace(path))
        {
            try
            {
                var description = FileVersionInfo.GetVersionInfo(path).FileDescription;

                if (!string.IsNullOrWhiteSpace(description))
                {
                    return description.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                // 读不到版本信息只是名字差一点，不影响可用性。
            }

            var name = Path.GetFileNameWithoutExtension(path);
            if (!string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return group.Aumid ?? "未知应用";
    }

    /// <summary>一组属于同一个应用的窗口。</summary>
    private sealed class Group(WindowIdentityInfo first)
    {
        public string? Aumid { get; } = first.EffectiveAumid;

        public string? ImagePath { get; } = first.ImagePath;

        public string IconSource => ImagePath ?? Aumid ?? string.Empty;

        public List<WindowIdentityInfo> Windows { get; } = [];
    }
}
