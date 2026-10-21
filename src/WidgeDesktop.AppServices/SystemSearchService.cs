using WidgeDesktop.Core.Models;
using WidgeDesktop.Platform.Shell;

namespace WidgeDesktop.AppServices;

/// <summary>搜索结果来自哪一类内容。</summary>
public enum SystemSearchKind
{
    /// <summary>已安装的应用（含打包应用与经典程序）。</summary>
    App = 0,

    /// <summary>索引里的本地文件。</summary>
    File = 1,
}

/// <summary>
/// 一条系统搜索结果。
/// </summary>
/// <remarks>
/// 与 <see cref="Core.Contracts.SearchHit"/> 的区别是它<strong>不代表应用库条目</strong>：
/// 这条记录来自系统（已安装应用或索引文件），库里可能根本没有它。
/// 用户按下「添加到启动台」之后才会变成一条 <see cref="AppEntry"/>。
/// </remarks>
public sealed class SystemSearchResult
{
    public SystemSearchKind Kind { get; init; }

    /// <summary>用户可见的名称。</summary>
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    /// 启动目标：应用是 <c>shell:AppsFolder\&lt;AUMID&gt;</c>，文件是完整路径。
    /// </summary>
    public string Target { get; init; } = string.Empty;

    /// <summary>应用身份；文件为 null。</summary>
    public string? AppUserModelId { get; init; }

    /// <summary>图标来源，可直接交给图标缓存。应用与文件都用它自己的目标。</summary>
    public string IconSource { get; init; } = string.Empty;
}

/// <summary>
/// 在系统范围内搜索：已安装应用 + 索引文件。
/// </summary>
/// <remarks>
/// <b>完全离线。</b>只用外壳的应用枚举与 Windows 搜索索引，不发任何网络请求——
/// 与产品「无遥测、不联网」的既有限制一致。
///
/// <para>
/// <b>两类来源分别容错。</b>应用枚举失败不影响文件搜索，反之亦然；
/// 任何一侧出问题都只是少了那一侧的结果，界面照常可用。
/// </para>
///
/// <para>
/// <b>排序与应用内搜索一致。</b>应用优先于文件，因为「按 Win 键搜索」最常见的意图是
/// 启动一个程序；名称前缀命中的排在包含命中之前。
/// </para>
/// </remarks>
public sealed class SystemSearchService
{
    private readonly Func<IReadOnlyList<InstalledApp>> _apps;
    private readonly Func<string, int, IReadOnlyList<IndexedFile>> _files;

    /// <summary>生产构造：接外壳的枚举器与索引查询器。</summary>
    public SystemSearchService()
        : this(InstalledAppScanner.Scan, (query, limit) => IndexedFileSearcher.Search(query, limit))
    {
    }

    /// <summary>测试构造：注入两个来源，用来验证合并、去重与排序规则。</summary>
    internal SystemSearchService(Func<IReadOnlyList<InstalledApp>> apps,
        Func<string, int, IReadOnlyList<IndexedFile>> files)
    {
        _apps = apps;
        _files = files;
    }

    /// <summary>
    /// 搜索系统内容。
    /// </summary>
    /// <param name="query">查询词。</param>
    /// <param name="limit">最多返回多少条。</param>
    /// <param name="includeApps">是否包含已安装应用。</param>
    /// <param name="includeFiles">是否包含本地文件。</param>
    public IReadOnlyList<SystemSearchResult> Search(string? query, int limit,
        bool includeApps = true, bool includeFiles = true)
    {
        var needle = Normalize(query);

        if (needle.Length == 0 || limit <= 0 || (!includeApps && !includeFiles))
        {
            return [];
        }

        var ranked = new List<(int Rank, string SortKey, SystemSearchResult Result)>();

        if (includeApps)
        {
            foreach (var app in _apps())
            {
                if (Match(app.DisplayName, app.Aumid, needle) is not { } rank)
                {
                    continue;
                }

                ranked.Add((rank, app.DisplayName, new SystemSearchResult
                {
                    Kind = SystemSearchKind.App,
                    DisplayName = app.DisplayName,
                    Target = app.LaunchTarget,
                    AppUserModelId = app.Aumid,
                    IconSource = app.IconSource,
                }));
            }
        }

        if (includeFiles)
        {
            foreach (var file in _files(needle, limit))
            {
                // 文件只按名称排序：内容命中无法解释成「前缀」，一律按包含处理，
                // 这样不会因为内容匹配就误排到应用前面。
                var rank = Match(file.Name, null, needle) is { } fileRank
                    ? fileRank
                    : (int)SystemSearchRank.FileContains;

                ranked.Add((1000 + rank, file.Name, new SystemSearchResult
                {
                    Kind = SystemSearchKind.File,
                    DisplayName = file.Name,
                    Target = file.Path,
                    IconSource = file.Path,
                }));
            }
        }

        return ranked
            .OrderBy(entry => entry.Rank)
            .ThenBy(entry => entry.SortKey, StringComparer.CurrentCulture)
            .Take(limit)
            .Select(entry => entry.Result)
            // 同一个目标只保留一条：应用枚举与文件搜索可能对同一路径各报一次。
            .DistinctBy(result => result.Target, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>命中等级；越小越靠前。null 表示没命中。</summary>
    private static int? Match(string name, string? aumid, string needle)
    {
        var normalized = Normalize(name);

        if (normalized == needle)
        {
            return (int)SystemSearchRank.ExactName;
        }

        if (normalized.StartsWith(needle, StringComparison.Ordinal))
        {
            return (int)SystemSearchRank.NamePrefix;
        }

        // 打包应用的显示名是本地化的（「计算器」），AUMID 才是英文标识
        // （Microsoft.WindowsCalculator_…）。两者都可搜，用户敲哪个都能找到。
        if (aumid is not null && Normalize(aumid).Contains(needle, StringComparison.Ordinal))
        {
            return (int)SystemSearchRank.IdentityContains;
        }

        return normalized.Contains(needle, StringComparison.Ordinal)
            ? (int)SystemSearchRank.NameContains
            : null;
    }

    private static string Normalize(string? text)
        => (text ?? string.Empty).Trim().ToLowerInvariant();
}

/// <summary>命中等级，与应用内搜索的 <c>SearchMatchKind</c> 同一套思路。</summary>
internal enum SystemSearchRank
{
    ExactName = 0,
    NamePrefix = 1,
    IdentityContains = 2,
    NameContains = 3,

    /// <summary>仅由文件内容命中、名称里没有查询词。排在最后。</summary>
    FileContains = 4,
}
