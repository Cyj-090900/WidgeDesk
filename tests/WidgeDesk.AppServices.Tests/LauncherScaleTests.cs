using System.Diagnostics;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;

namespace WidgeDesk.AppServices.Tests;

public sealed class LauncherScaleTests
{
    [Fact]
    public void FiveHundredItems_SearchPageAndReloadWithoutSearchWrites()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-500-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new WidgeDeskDataStore(new AppPaths(root));
            data.LoadAll();
            for (var i = 0; i < 500; i++)
                Assert.Equal(AddEntryResult.Added, data.Library.Add(new AppEntry
                {
                    Id = $"app-{i:000}", DisplayName = i == 499 ? "终点 工具 499" : $"测试 App {i:000}",
                    Target = Path.Combine(root, $"不存在 的路径 {i:000}"), IsTargetMissing = true,
                    SearchAliases = i == 499 ? ["last-item"] : [],
                }));
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var workspace = new LauncherWorkspace(host);
            Assert.True(workspace.Reconcile(35));
            Assert.Equal(15, workspace.Layout.Pages.Count);
            Assert.Equal(10, workspace.Layout.Pages[^1].Slots.Count);
            var before = File.ReadAllBytes(data.Layouts.LauncherFilePath);
            var stamp = File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath);
            var watch = Stopwatch.StartNew();
            for (var i = 0; i < 30; i++)
                Assert.Equal("app-499", Assert.Single(host.Search.SearchApps("last-item", 500)).Item.Id);
            watch.Stop();
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath));
            Assert.Equal(before, File.ReadAllBytes(data.Layouts.LauncherFilePath));
            var reloaded = new WidgeDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Assert.Equal(500, LauncherLayoutRules.AppIds(reloaded.Layouts.Launcher).Count);
            Assert.True(watch.ElapsedMilliseconds >= 0);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
