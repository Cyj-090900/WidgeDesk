using System.Diagnostics;
using Windows.Storage;
using Windows.Storage.Search;

namespace WidgeDesktop.Platform.Shell;

/// <summary>索引里的一条文件结果。</summary>
/// <param name="Path">文件的完整路径。</param>
/// <param name="Name">不带目录的文件名，用于显示。</param>
public sealed record IndexedFile(string Path, string Name);

/// <summary>
/// 通过 Windows 搜索索引查找本地文件。
/// </summary>
/// <remarks>
/// <b>为什么用索引而不是自己遍历目录。</b>遍历整个用户目录要几十秒，而且只能按文件名匹配；
/// 索引查询由系统维护，毫秒级返回，并且能命中文件<strong>内容</strong>
/// （实测搜「会话」能找出内容里含该词的文档）。这也正是「按 Win 键搜索」用的那条路径。
///
/// <para>
/// <b>查询词原样交给 <c>UserSearchFilter</c>。</b>实测直接传「微信」「notepad」都能正常
/// 匹配，反而自己拼成 <c>System.FileName:"…"</c> 会缩小命中面——系统已经按查询语法解析它。
/// </para>
///
/// <para>
/// <b>范围限定在用户自己的目录。</b>对整个盘符查询会把系统目录、其它用户目录一起翻出来，
/// 既慢又噪。这里只查用户配置文件下的常用位置。
/// </para>
///
/// <para>
/// 索引服务停止、目录不可访问、查询语法被拒——一律返回空列表。搜索是辅助功能，
/// 它失败不该让界面报错。
/// </para>
/// </remarks>
public static class IndexedFileSearcher
{
    /// <summary>单次查询最多取回的条数。结果集再大也没有意义，界面只显示得下前几页。</summary>
    private const int MaxResultsPerScope = 40;

    /// <summary>索引正在重建或不可用时，系统可能长时间不返回；给一个上限。</summary>
    private static readonly TimeSpan ScopeBudget = TimeSpan.FromSeconds(3);

    /// <summary>
    /// 在用户的常用目录里按索引查找文件。
    /// </summary>
    /// <param name="query">查询词，原样交给系统。</param>
    /// <param name="limit">最多返回多少条。</param>
    public static IReadOnlyList<IndexedFile> Search(string? query, int limit)
    {
        if (limit <= 0 || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }

        var needle = query.Trim();
        var results = new List<IndexedFile>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // SearchAsync 是 async 的，但这里的调用点（按键触发的后台查询）不希望引入
        // async 传染，所以同步等待一个已经带超时的任务。等待本身在后台线程上发生。
        var task = Task.Run(() => SearchScopesAsync(needle, limit, results, seen));

        try
        {
            task.Wait(ScopeBudget + TimeSpan.FromSeconds(2));
        }
        catch (AggregateException ex) when (ex.InnerException is not OutOfMemoryException)
        {
            // 索引查询失败：返回已经拿到的部分。
        }

        return results;
    }

    private static async Task SearchScopesAsync(string needle, int limit, List<IndexedFile> results,
        HashSet<string> seen)
    {
        foreach (var folder in EnumerateScopes())
        {
            if (results.Count >= limit)
            {
                return;
            }

            try
            {
                var storageFolder = await StorageFolder.GetFolderFromPathAsync(folder);

                var options = new QueryOptions(CommonFileQuery.OrderBySearchRank, ["*"])
                {
                    // 索引可用时走索引（快、能匹配内容）；不可用时退回普通枚举，
                    // 至少还能按文件名找到东西。
                    IndexerOption = IndexerOption.UseIndexerWhenAvailable,
                    UserSearchFilter = needle,
                };

                var files = await storageFolder.CreateFileQueryWithOptions(options)
                    .GetFilesAsync(0, (uint)Math.Min(MaxResultsPerScope, limit - results.Count));

                foreach (var file in files)
                {
                    if (results.Count >= limit)
                    {
                        return;
                    }

                    if (!string.IsNullOrWhiteSpace(file.Path) && seen.Add(file.Path))
                    {
                        results.Add(new IndexedFile(file.Path, file.Name));
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // 这个目录查不了（索引关闭、权限、路径被重定向……），跳过它继续下一个。
            }
        }
    }

    /// <summary>
    /// 要搜索的目录。
    /// </summary>
    /// <remarks>
    /// 用 <see cref="KnownFolders"/> 解析，而不是拼 <c>%USERPROFILE%</c>：这些位置
    /// 可能被 OneDrive 或企业策略重定向，拼字符串在那些机器上会指向不存在的地方
    /// （与 <see cref="KnownFolders"/> 自己的说明同理）。
    /// </remarks>
    private static IEnumerable<string> EnumerateScopes()
    {
        var candidates = new[]
        {
            KnownFolders.Desktop,
            KnownFolders.Documents,
            KnownFolders.Downloads,
            KnownFolders.UserProfile,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate) && seen.Add(candidate) && Directory.Exists(candidate))
            {
                yield return candidate;
            }
        }
    }
}
