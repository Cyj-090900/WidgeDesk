using System.IO;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;

namespace WidgeDesk.Dock.Tests;

public sealed class BackgroundAppTests
{
    private static AppLibraryStore Library(string root)
    {
        var data = new WidgeDeskDataStore(new AppPaths(root));
        data.LoadAll();
        return data.Library;
    }

    [Fact]
    public void TrayOnlyProgramsBecomeAutoTilesBecauseTheyHaveNoWindow()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgcat-" + Guid.NewGuid().ToString("N"));
        try
        {
            var apps = BackgroundAppCatalog.Build(
                [new TrayApp(4242, @"D:\Apps\Spotify.exe", "Spotify")],
                Library(root), [], [], [], 1);

            var app = Assert.Single(apps);
            Assert.StartsWith(RunningAppCatalog.AutoTilePrefix, app.Id);
            Assert.Equal("Spotify", app.DisplayName);
            Assert.Equal(@"D:\Apps\Spotify.exe", app.Target);
            // 没有窗口：点击只能走启动/激活，不能去"激活某个窗口"。
            Assert.Empty(app.Windows);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AlreadyPinnedOrAlreadyTiledProgramsAreNotDuplicated()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgdedup-" + Guid.NewGuid().ToString("N"));
        try
        {
            var library = Library(root);
            var entry = new WidgeDesk.Core.Models.AppEntry
            {
                Id = "pinned-feishu",
                DisplayName = "Feishu",
                Target = @"D:\Feishu\app\Feishu.exe",
            };
            library.Add(entry);

            // 已经固定在 Dock 上的不能再来一条。
            var coveredById = BackgroundAppCatalog.Build(
                [new TrayApp(1, @"D:\Feishu\app\Feishu.exe", "Feishu")],
                library, [entry.Id], [], [], 1);
            Assert.Empty(coveredById);

            // 窗口类自动磁贴已经覆盖同一目标时也不能重复。
            var coveredByTarget = BackgroundAppCatalog.Build(
                [new TrayApp(1, @"D:\Feishu\app\Feishu.exe", "Feishu")],
                library, [], [@"D:\Feishu\app\Feishu.exe"], [], 1);
            Assert.Empty(coveredByTarget);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SystemHostProcessesAndOwnProcessAreExcluded()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bghost-" + Guid.NewGuid().ToString("N"));
        try
        {
            var apps = BackgroundAppCatalog.Build(
                [
                    new TrayApp(1, @"C:\Windows\System32\ApplicationFrameHost.exe", "ApplicationFrameHost"),
                    new TrayApp(2, @"C:\Windows\System32\RuntimeBroker.exe", "RuntimeBroker"),
                    new TrayApp(3, null, "未知"),
                    new TrayApp(4, @"D:\Apps\Mine.exe", "Mine"),
                ],
                Library(root), [], [], [], 4);

            Assert.Empty(apps);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SuppressedBackgroundAppsStayHidden()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgsup-" + Guid.NewGuid().ToString("N"));
        try
        {
            const string target = @"D:\Apps\Mine.exe";
            var id = RunningAppCatalog.AutoTilePrefix + target.ToLowerInvariant();
            var apps = BackgroundAppCatalog.Build(
                [new TrayApp(7, target, "Mine")],
                Library(root), [], [], [id], 1);
            Assert.Empty(apps);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void ReadingTheRealNotificationAreaNeverThrows()
    {
        // 这台机器上 explorer 可能在重启、也可能没有后台程序。
        // 契约只有一条：任何情况下都返回一个（可能为空的）快照，而不是抛异常。
        var apps = TrayAppScanner.Read((uint)Environment.ProcessId);
        Assert.NotNull(apps);
        Assert.All(apps, app => Assert.NotEqual(0, app.ProcessId));
    }

    [Fact]
    public void TrayScanIncludesIconsHiddenInTheOverflowPanel()
    {
        // 本机实测：可见托盘区 6 枚、溢出面板 8 枚，去重后共 10 个后台程序。
        // 只读可见托盘区时只能拿到 6 个，所以下面这条数量断言会失败——
        // 它钉住的正是"溢出面板也参与了扫描"。
        var apps = TrayAppScanner.Read((uint)Environment.ProcessId);
        Assert.True(apps.Count >= 9,
            $"只读到 {apps.Count} 个后台程序；溢出面板里的图标很可能没被读到");

        // 每个读到的图标都必须带可用的回调地址，否则右键无法复用应用菜单。
        Assert.All(apps, app =>
        {
            Assert.NotEqual(0, app.ProcessId);
            Assert.True(app.IconWindow == 0 || app.CallbackMessage != 0,
                $"{app.DisplayName} 有窗口却没有回调消息");
        });
    }
}
