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
    public void DockSizeAndFrameLengthNoLongerHaveSettingsControls()
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
                // 尺寸与外框长度不再可调：页面上没有控件，Dock 固定用默认尺寸。
                Assert.Null(settings.FindName("DockScaleSlider"));
                Assert.Null(settings.FindName("DockFrameLengthSlider"));
                Assert.Null(settings.FindName("DockFrameLengthReadout"));
                Assert.Equal(48, Assert.IsType<Button>(dock.FindName("LauncherButton")).Width, 1);

                // 旧文件里写过的自定义尺寸会被归一，而不是悄悄影响外观。
                data.Settings.Current.Dock.SizeScale = .75;
                data.Settings.Current.Dock.FrameLengthDip = 900;
                data.Settings.Save();
                var reload = new SettingsStore(data.Paths.SettingsFile);
                reload.Load();
                Assert.Equal(1, reload.Current.Dock.SizeScale);
                Assert.Equal(0, reload.Current.Dock.FrameLengthDip);

                // 其它 Dock 设置仍然照常保存。
                Assert.IsType<Slider>(settings.FindName("DockOffsetXSlider")).Value = 20;
                var offsetReload = new SettingsStore(data.Paths.SettingsFile);
                offsetReload.Load();
                Assert.Equal(20, offsetReload.Current.Dock.OffsetX);
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
