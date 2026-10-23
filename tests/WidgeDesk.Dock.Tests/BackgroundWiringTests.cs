using System.IO;
using System.Windows;
using System.Windows.Controls;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;
using WidgeDesk.UI.Settings;

namespace WidgeDesk.Dock.Tests;

public sealed class BackgroundWiringTests
{
    [Fact]
    public void TrayAppsReachTheDockAsAutoTiles()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgwire-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow
            {
                // 换掉真实扫描：这条用例验证的是"接线"，不是这台机器上恰好开着什么。
                ReadTrayApps = () => [new TrayApp(9001, @"D:\Apps\Background.exe", "Background App")],
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

                // 后台应用在右侧那条里，不占左侧"我固定的应用"。
                Assert.Equal(0, dock.TileCount);
                var strip = (DockTileStrip)typeof(DockWindow)
                    .GetField("_backgroundStrip", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                    .GetValue(dock)!;
                var tile = Assert.Single(strip.PaintedTiles);
                Assert.Equal(RunningAppCatalog.AutoTilePrefix + @"d:\apps\background.exe", tile.EntryId);

                // 后台条必须排在托盘区之前、主条之后：这就是"在右边"的定义。
                var main = Assert.IsType<ContentControl>(dock.FindName("TileStripHost"));
                var row = Assert.IsType<StackPanel>(main.Parent);
                var backgroundHost = Assert.IsType<ContentControl>(dock.FindName("BackgroundStripHost"));
                var tray = Assert.IsType<Border>(dock.FindName("TrayArea"));
                Assert.True(row.Children.IndexOf(backgroundHost) > row.Children.IndexOf(main));
                Assert.True(row.Children.IndexOf(tray) > row.Children.IndexOf(backgroundHost));
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void DocksWithoutAnyTrayAppsStayUnchanged()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-bgnone-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow { ReadTrayApps = () => [] };
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
                Assert.Equal(0, dock.TileCount);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
