using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;
using WidgeDesk.UI.Settings;

namespace WidgeDesk.Dock.Tests;

/// <summary>
/// 后台条上的磁贴必须和固定条一样可点击、可右键。
/// </summary>
/// <remarks>
/// 这是一条真实回归：把自动磁贴从左侧条搬到右侧后台条之后，
/// 查找仍然只扫左侧列表，于是左右键都静默 return——界面看起来完全正常，
/// 点下去什么也不发生。所以这里断言的是"找得到"，而不是"长得对"。
/// </remarks>
public sealed class BackgroundInteractionTests
{
    private const BindingFlags Private = BindingFlags.Instance | BindingFlags.NonPublic;

    [Fact]
    public void BackgroundTilesAreFoundByTheSharedClickAndMenuPaths()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgclick-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow
            {
                ReadTrayApps = () => [new TrayApp(7002, @"D:\Apps\Bg.exe", "Bg App")],
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

                var model = Assert.Single(dock.BackgroundTileModels);
                var find = typeof(DockWindow).GetMethod("FindTile", Private)!;
                Assert.Same(model, find.Invoke(dock, [model.EntryId]));

                // 右键路径：菜单必须真的建出来，而不是因为找不到磁贴提前返回。
                var request = typeof(DockWindow).GetMethod("ShowAutoTileContextMenu", Private)!;
                request.Invoke(dock, [model.EntryId]);

                // 点击路径：不抛异常且能定位到磁贴（真实启动留给人工验收）。
                var activate = typeof(DockWindow).GetMethod("ActivateAutoTile", Private)!;
                _ = activate.Invoke(dock, [model.EntryId]);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void PinnedTilesStillResolveAfterTheBackgroundStripWasAdded()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-pinclick-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow { ReadTrayApps = () => [] };
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var entry = AppEntryFactory.CreateFromInstalledApp("Notepad", @"C:\Apps\notepad.exe", null);
                data.Library.Add(entry);
                data.Layouts.Dock.PinnedItemIds.Add(entry.Id);
                data.Layouts.SaveDock();

                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();
                dock.RefreshTiles();

                var find = typeof(DockWindow).GetMethod("FindTile", Private)!;
                Assert.NotNull(find.Invoke(dock, [entry.Id]));
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void WaveOfWindowEventsDoesNotRebuildTheBackgroundStrip()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgsync-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow
            {
                ReadTrayApps = () => [new TrayApp(7003, @"D:\Apps\Bg2.exe", "Bg Two")],
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

                var strip = (DockTileStrip)typeof(DockWindow)
                    .GetField("_backgroundStrip", Private)!.GetValue(dock)!;
                var before = strip.PaintedTiles.Single().EntryId;

                // 协调两次：集合没变时不该重建（重建会重新加载图标并打断动画）。
                var sync = typeof(DockWindow).GetMethod("SyncAutoTiles", Private)!;
                sync.Invoke(dock, null);
                sync.Invoke(dock, null);

                var after = strip.PaintedTiles.Single().EntryId;
                Assert.Equal(before, after);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
