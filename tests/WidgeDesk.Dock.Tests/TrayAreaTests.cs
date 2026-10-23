using System.IO;
using System.Windows;
using System.Windows.Controls;
using WidgeDesk.AppServices;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;
using WidgeDesk.UI.Settings;

namespace WidgeDesk.Dock.Tests;

public sealed class TrayAreaTests
{
    [Fact]
    public void DockExposesNetworkVolumeAndClockOnTheRightSide()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-tray-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();

                var area = Assert.IsType<Border>(dock.FindName("TrayArea"));
                var strip = Assert.IsType<ContentControl>(dock.FindName("TileStripHost"));
                Assert.Equal(Visibility.Visible, area.Visibility);
                // 托盘必须是应用磁贴之后的那一项，不能混进可排序的应用区。
                var row = Assert.IsType<StackPanel>(area.Parent);
                Assert.Same(area, row.Children[^1]);
                Assert.True(row.Children.IndexOf(area) > row.Children.IndexOf(strip));

                foreach (var name in new[] { "NetworkButton", "VolumeButton", "ClockButton" })
                    Assert.True(Assert.IsType<Button>(dock.FindName(name)).IsEnabled, name);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void ClockShowsTheCurrentLocalTimeAndDate()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-trayclock-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();
                dock.RefreshTray();

                var now = DateTime.Now;
                Assert.Equal(now.ToString("HH:mm", System.Globalization.CultureInfo.CurrentCulture),
                    Assert.IsType<TextBlock>(dock.FindName("ClockTime")).Text);
                Assert.Equal(now.ToString("yyyy/M/d", System.Globalization.CultureInfo.CurrentCulture),
                    Assert.IsType<TextBlock>(dock.FindName("ClockDate")).Text);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void TrayReadsTheRealNetworkAndVolumeInsteadOfFakeState()
    {
        var network = TrayStatusReader.ReadNetwork();
        Assert.True(Enum.IsDefined(network.Kind));
        if (network.Kind != NetworkKind.Offline)
            Assert.False(string.IsNullOrWhiteSpace(network.Name));

        // 没有音频设备时这里为 null；有设备就必须落在合法区间。
        var volume = TrayStatusReader.ReadVolume();
        if (volume is not null)
            Assert.InRange(volume.Level, 0, 1);
    }

    [Fact]
    public void MutedOrSilentVolumeHidesTheWaveAndShowsTheCross()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-traymute-" + Guid.NewGuid().ToString("N"));
            var dock = new DockWindow();
            try
            {
                var data = new WidgeDeskDataStore(new AppPaths(root));
                data.LoadAll();
                using var services = new AppServiceHost(data);
                var host = DockSettingsHost.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
                dock.AttachServices(services, host, () => { });
                dock.Show();
                dock.UpdateLayout();

                var wave = (UIElement)dock.FindName("VolumeWave");
                var cross = (UIElement)dock.FindName("VolumeMutedCross");
                if (dock.CurrentVolume is null)
                {
                    // 没有播放设备：图标必须明确表示不可用，而不是画一个"有声音"的样子。
                    Assert.Equal(Visibility.Visible, cross.Visibility);
                    Assert.False(((Button)dock.FindName("VolumeButton")).IsEnabled);
                    return;
                }

                // 正常与静音两种形态必须互斥，不能同时出现。
                Assert.NotEqual(wave.Visibility, cross.Visibility);
            }
            finally
            {
                dock.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }
}
