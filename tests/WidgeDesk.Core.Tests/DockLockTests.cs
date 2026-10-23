using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;

namespace WidgeDesk.Core.Tests;

public sealed class DockLockTests
{
    private static LayoutStore Store(TempDataDirectory temp)
        => new(temp.Paths.DockLayoutFile, temp.Paths.LauncherLayoutFile);

    [Fact]
    public void LockingAlsoPinsSoTheIconIsAlwaysVisible()
    {
        using var temp = new TempDataDirectory();
        var store = Store(temp);
        store.LoadDock();

        Assert.True(store.LockToDock("app-1"));
        Assert.True(store.IsLocked("app-1"));
        // 锁定必须隐含固定，否则会出现"看不见却移不掉"的自相矛盾状态。
        Assert.Contains("app-1", store.Dock.PinnedItemIds);
    }

    [Fact]
    public void LockedItemsRefuseUnpinUntilUnlocked()
    {
        using var temp = new TempDataDirectory();
        var store = Store(temp);
        store.LoadDock();
        store.LockToDock("app-1");

        // 这一条就是"退出/移除都不会消失"的保证：所有移除路径都经过这里。
        Assert.False(store.UnpinFromDock("app-1"));
        Assert.Contains("app-1", store.Dock.PinnedItemIds);

        Assert.True(store.UnlockFromDock("app-1"));
        // 解锁只放开保证，图标仍在 Dock 上。
        Assert.Contains("app-1", store.Dock.PinnedItemIds);
        Assert.True(store.UnpinFromDock("app-1"));
        Assert.DoesNotContain("app-1", store.Dock.PinnedItemIds);
    }

    [Fact]
    public void LockSurvivesSavingAndReloading()
    {
        using var temp = new TempDataDirectory();
        var store = Store(temp);
        store.LoadDock();
        store.LockToDock("app-1");
        store.SaveDock();

        var reloaded = Store(temp);
        reloaded.LoadDock();
        Assert.True(reloaded.IsLocked("app-1"));
        Assert.Contains("app-1", reloaded.Dock.PinnedItemIds);
        Assert.False(reloaded.UnpinFromDock("app-1"));
    }

    [Fact]
    public void AFailedSaveRestoresThePreviousLockState()
    {
        using var temp = new TempDataDirectory();
        var store = Store(temp);
        store.LoadDock();
        store.SaveDock();

        store.LockToDock("app-1");

        // 占住原子写用的临时文件，让这次写入必然失败（与 Dock 持久化测试同一手法）。
        using (var blocker = new FileStream(temp.Paths.DockLayoutFile + AtomicFile.TempSuffix,
            FileMode.Create, FileAccess.ReadWrite, FileShare.None))
        {
            Assert.ThrowsAny<Exception>(store.SaveDock);
        }

        // 写盘失败后内存里不能留下"看起来锁好了、实际没保存"的状态。
        Assert.False(store.IsLocked("app-1"));
        Assert.DoesNotContain("app-1", store.Dock.PinnedItemIds);
    }

    [Fact]
    public void OldLayoutFilesWithoutTheLockFieldStillLoad()
    {
        using var temp = new TempDataDirectory();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Paths.DockLayoutFile)!);
        File.WriteAllText(temp.Paths.DockLayoutFile, """{"pinnedItemIds":["app-1"]}""");

        var store = Store(temp);
        var outcome = store.LoadDock();
        Assert.Equal(WidgeDesk.Core.Storage.LoadStatus.Loaded, outcome.Status);
        Assert.Empty(store.Dock.LockedItemIds);
        Assert.Contains("app-1", store.Dock.PinnedItemIds);
    }
}
