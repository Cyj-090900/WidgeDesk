using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Threading;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.Core.Storage;
using WidgeDesk.AppServices;
using WidgeDesk.Settings;
using WidgeDesk.UI.Controls;
using WidgeDesk.UI.Settings;
using WidgeDesk.UI.Theming;

namespace WidgeDesk.Dock.Tests;

public sealed class AppearanceCustomizationTests
{
    [Fact]
    public void OpenPlacementMenuHasAnOpaqueBodyAndMatchesTheSelectionWidth()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var window = new SettingsWindow();
            window.Resources.MergedDictionaries.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/WidgeDesk.UI;component/Theming/LiquidGlassTheme.xaml")
            });
            try
            {
                window.Show(); window.UpdateLayout();
                var combo = Assert.IsType<ComboBox>(window.FindName("DockPlacementCombo"));
                combo.BringIntoView(); window.UpdateLayout();
                combo.IsDropDownOpen = true;
                window.UpdateLayout();
                var popup = Assert.IsType<Popup>(combo.Template.FindName("PART_Popup", combo));
                var body = Assert.IsType<PhoneBorder>(popup.Child);
                body.UpdateLayout();
                Assert.InRange(Assert.IsType<SolidColorBrush>(body.Background).Color.A, 250, 255);
                Assert.True(body.ActualWidth >= combo.ActualWidth - 1,
                    $"Menu {body.ActualWidth}, selection {combo.ActualWidth}");
            }
            finally { window.Close(); }
        });
    }

    [Fact]
    public void SizeSliderUpdatesLiveDockAndPersistsAcrossOtherDockChanges()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-customize-" + Guid.NewGuid().ToString("N"));
            var data = new WidgeDeskDataStore(new AppPaths(root));
            data.LoadAll();
            var host = DockSettingsHost.Initialize(Dispatcher.CurrentDispatcher);
            host.Update(data.Settings.Current.Dock);
            var settings = new SettingsWindow();
            var dock = new DockWindow();
            using var services = new AppServiceHost(data);
            try
            {
                settings.AttachDataStore(data, null);
                dock.AttachServices(services, host, () => { });
                dock.Show(); dock.UpdateLayout();
                var originalHeight = dock.ActualHeight;
                var slider = Assert.IsType<Slider>(settings.FindName("DockScaleSlider"));
                slider.Value = .75;
                dock.UpdateLayout();
                Assert.Equal(36, Assert.IsType<Button>(dock.FindName("LauncherButton")).Width);
                Assert.InRange(dock.ActualHeight / originalHeight, .72, .78);
                Assert.IsType<Slider>(settings.FindName("DockOffsetXSlider")).Value = 20;
                var reload = new SettingsStore(data.Paths.SettingsFile); reload.Load();
                Assert.Equal(.75, typeof(DockSettings).GetProperty("SizeScale")?.GetValue(reload.Current.Dock));
                Assert.Equal(20, reload.Current.Dock.OffsetX);
                Assert.Equal(36, Assert.IsType<Button>(dock.FindName("LauncherButton")).Width);
            }
            finally
            {
                dock.Close(); settings.Close();
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
        });
    }

    [Fact]
    public void AppearancePageNoLongerOffersAMaterialSelectorAndNormalizesLegacyValues()
    {
        StaTest.Run(() =>
        {
            var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-customize-" + Guid.NewGuid().ToString("N"));
            using var theme = ThemeService.Initialize(Dispatcher.CurrentDispatcher);
            var data = new WidgeDeskDataStore(new AppPaths(root)); data.LoadAll();
            var window = new SettingsWindow();
            try
            {
                // 旧文件可能写着磨砂；加载时必须归一成液态玻璃。
                data.Settings.Current.Appearance.GlassMaterial = GlassMaterialMode.FrostedGlass;
                data.Settings.Save();
                var reloaded = new SettingsStore(data.Paths.SettingsFile); reloaded.Load();
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, reloaded.Current.Appearance.GlassMaterial);
                window.AttachDataStore(data, null);
                Assert.Null(window.FindName("AppearanceMaterialCombo"));
                Assert.Null(window.FindName("GlassDebugViewCombo"));
                Assert.IsType<CheckBox>(window.FindName("ReduceMotionCheck")).IsChecked = true;
                Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, theme.Appearance.GlassMaterial);
            }
            finally { window.Close(); if (Directory.Exists(root)) Directory.Delete(root, true); }
        });
    }

}
