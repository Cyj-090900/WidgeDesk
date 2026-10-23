using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;
using WidgeDesk.UI.Settings;

namespace WidgeDesk.Dock.Tests;

public sealed class DockLockMenuTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void PinnedTileMenuOffersLockAndHidesRemoveWhileLocked()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-lockmenu-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow { ReadTrayApps = () => [] };
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var entry = AppEntryFactory.CreateFromInstalledApp("Notepad", @"C:\Apps\notepad.exe", null);
                data.Library.Add(entry);
                data.Layouts.LockToDock(entry.Id);
                data.Layouts.SaveDock();

                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();

                Assert.True(data.Layouts.IsLocked(entry.Id));

                // 菜单契约：锁定时不再提供「从 Dock 移除」，并给出解锁项。
                var lockedMenu = dock.BuildTileMenu(entry);
                var lockedHeaders = lockedMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToList();
                Assert.Contains("解锁（允许移除）", lockedHeaders);
                Assert.DoesNotContain("从 Dock 移除", lockedHeaders);

                data.Layouts.UnlockFromDock(entry.Id);
                data.Layouts.SaveDock();
                var unlockedMenu = dock.BuildTileMenu(entry);
                var unlockedHeaders = unlockedMenu.Items.OfType<MenuItem>().Select(item => item.Header?.ToString()).ToList();
                Assert.Contains("锁定在 Dock", unlockedHeaders);
                Assert.Contains("从 Dock 移除", unlockedHeaders);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void FixedAppSurvivesTrayAndWindowDisappearing()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-locksurvive-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow { ReadTrayApps = () => [] };
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var entry = AppEntryFactory.CreateFromInstalledApp("Notepad", @"C:\Apps\notepad.exe", null);
                data.Library.Add(entry);
                data.Layouts.LockToDock(entry.Id);
                data.Layouts.SaveDock();

                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();
                dock.RefreshTiles();
                Assert.Equal(1, dock.TileCount);

                // 应用没在运行（没有窗口、没有通知区图标）时，图标仍然在。
                data.Library.Find(entry.Id)!.IsTargetMissing = false;
                dock.RefreshTiles();
                Assert.Equal(1, dock.TileCount);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void PinAutoTileAddsTheBackgroundAppToTheLibraryAndLocksIt()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-pinauto-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow
            {
                ReadTrayApps = () => [new Platform.Shell.TrayApp(7001, @"D:\Apps\Background.exe", "Background App")],
            };
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();
                dock.RefreshTiles();

                var model = dock.BackgroundTileModels.Single(tile => tile.IsAuto);
                var pin = typeof(DockWindow).GetMethod("PinAutoTile", Private)!;
                pin.Invoke(dock, [model]);

                var entry = data.Library.Items.Single(item => item.Target == @"D:\Apps\Background.exe");
                Assert.True(data.Layouts.IsLocked(entry.Id));
                Assert.Contains(entry.Id, data.Layouts.Dock.PinnedItemIds);
                // 固定之后它不再是自动项（否则退出后仍会被自动收走）。
                Assert.All(dock.BackgroundTileModels, tile => Assert.False(tile.IsAuto));
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
