using Widge.AppServices;
using Widge.Core.Models;
using Widge.Core.Storage;

namespace Widge.AppServices.Tests;

public sealed class LauncherWorkspaceTests
{
    [Fact]
    public void Reconcile_UsesSharedLibraryAndDoesNotRewriteUnchangedLayout()
    {
        var root = Path.Combine(Path.GetTempPath(), "Widge-LauncherWorkspace-" + Guid.NewGuid().ToString("n"));
        try
        {
            var data = new WidgeDataStore(new AppPaths(root));
            data.LoadAll();
            data.Library.Add(new AppEntry { Id = "a", DisplayName = "A", Target = "missing", IsTargetMissing = true });
            data.Library.Save();
            using var host = new AppServiceHost(data);
            var workspace = new LauncherWorkspace(host);
            Assert.True(workspace.Reconcile(35));
            Assert.Equal("a", data.Layouts.Launcher.Pages[0].Slots[0].ItemId);
            var stamp = File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath);
            Assert.False(workspace.Reconcile(35));
            Assert.Equal(stamp, File.GetLastWriteTimeUtc(data.Layouts.LauncherFilePath));
            Assert.Empty(data.Layouts.Dock.PinnedItemIds);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
