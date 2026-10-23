using System.IO;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;

namespace WidgeDesk.AppServices;

/// <summary>
/// 把通知区里的后台程序归纳成可以放上 Dock 的条目。
/// </summary>
/// <remarks>
/// <para>
/// <b>补的是哪一块。</b><see cref="RunningAppCatalog"/> 只看有顶层窗口的应用。
/// 像 WPS 云服务、飞书、网盘这类常驻程序平时把窗口收进通知区，按窗口枚举
/// 永远看不到它们，Dock 上就少了这些"确实正在运行"的应用。
/// </para>
/// <para>
/// <b>纯函数。</b>输入是通知区快照与应用库，输出是磁贴原料；不碰窗口、不落盘，
/// 与 <see cref="RunningAppCatalog"/> 同样的理由：可以用构造出来的输入单测。
/// </para>
/// </remarks>
public static class BackgroundAppCatalog
{
    /// <summary>
    /// 归纳后台应用。
    /// </summary>
    /// <param name="trayApps">通知区快照。</param>
    /// <param name="library">应用库：有记录时沿用它的名称、图标与启动方式。</param>
    /// <param name="coveredEntryIds">已有磁贴的条目 id（固定项与窗口类自动项）。</param>
    /// <param name="coveredTargets">已经出现过磁贴的启动目标，用于跨来源去重。</param>
    /// <param name="suppressedAutoIds">用户从 Dock 移除过、本次不再出现的自动项 id。</param>
    /// <param name="ownProcessId">本进程 id。</param>
    public static IReadOnlyList<RunningApp> Build(
        IReadOnlyList<TrayApp> trayApps,
        AppLibraryStore library,
        IReadOnlyCollection<string> coveredEntryIds,
        IReadOnlyCollection<string> coveredTargets,
        IReadOnlyCollection<string> suppressedAutoIds,
        uint ownProcessId)
    {
        ArgumentNullException.ThrowIfNull(trayApps);
        ArgumentNullException.ThrowIfNull(library);
        ArgumentNullException.ThrowIfNull(coveredEntryIds);
        ArgumentNullException.ThrowIfNull(coveredTargets);
        ArgumentNullException.ThrowIfNull(suppressedAutoIds);

        var covered = coveredEntryIds as ISet<string> ?? coveredEntryIds.ToHashSet(StringComparer.Ordinal);
        var targets = coveredTargets as ISet<string> ?? coveredTargets.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var suppressed = suppressedAutoIds as ISet<string> ?? suppressedAutoIds.ToHashSet(StringComparer.Ordinal);

        var result = new List<RunningApp>();
        var seenTargets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var app in trayApps)
        {
            // 通知区里偶尔会出现共享宿主进程（例如 UWP 的 ApplicationFrameHost）。
            // 它本身不是用户会去启动的程序，放上 Dock 也没有意义。
            if (app.ProcessId == ownProcessId) continue;
            if (string.IsNullOrWhiteSpace(app.ExecutablePath)) continue;
            if (IsHostProcess(app.ExecutablePath)) continue;
            if (!seenTargets.Add(app.ExecutablePath)) continue;

            var entry = FindEntry(library, app);
            if (entry is not null && covered.Contains(entry.Id)) continue;

            var target = entry?.Target ?? app.ExecutablePath;
            // 窗口类自动项已经为同一个程序出过磁贴时不要再加一枚。
            if (targets.Contains(target) || targets.Contains(app.ExecutablePath)) continue;

            var id = RunningAppCatalog.AutoTilePrefix + target.ToLowerInvariant();
            if (suppressed.Contains(id)) continue;

            result.Add(new RunningApp(
                id,
                entry?.DisplayName ?? app.DisplayName,
                target,
                null,
                entry?.IconSource ?? app.ExecutablePath,
                entry?.CustomIconPath,
                entry?.Id,
                // 没有窗口：点击走「启动或激活」，不会误把别的窗口提到前面。
                [],
                []));
        }

        return result;
    }

    /// <summary>
    /// 这些映像只是别的应用的宿主，不该单独出现在 Dock 上。
    /// </summary>
    /// <remarks>
    /// 这份名单刻意很短：只收那些「本身没有用户界面、只承载别人的窗口/图标」的
    /// 系统宿主。把范围放大成"看起来像系统的程序"会让真正的系统工具也没法上 Dock。
    /// </remarks>
    private static bool IsHostProcess(string path)
    {
        var name = Path.GetFileName(path);
        return name.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("RuntimeBroker.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("ShellExperienceHost.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("SearchApp.exe", StringComparison.OrdinalIgnoreCase)
            || name.Equals("TextInputHost.exe", StringComparison.OrdinalIgnoreCase);
    }

    private static AppEntry? FindEntry(AppLibraryStore library, TrayApp app)
    {
        var path = app.ExecutablePath;
        if (string.IsNullOrWhiteSpace(path)) return null;

        return library.Items.FirstOrDefault(item => item is { IsUsable: true }
            && !string.IsNullOrWhiteSpace(item.Target)
            && string.Equals(item.Target, path, StringComparison.OrdinalIgnoreCase));
    }
}
