using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WidgeDesk.Core.Models;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Displays;
using WidgeDesk.Settings;
using WidgeDesk.UI.Controls;
using WidgeDesk.UI.Theming;
using ThemeMode = WidgeDesk.Core.Models.ThemeMode;

namespace WidgeDesk.Dock.Tests;

public sealed class CustomBackgroundTests
{
    [Fact]
    public void CustomBackgroundReplacesDesktopCaptureWithTheChosenColour()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings
            {
                Theme = ThemeMode.Dark,
                UseCustomBackground = true,
                CustomBackgroundColor = "#FF3B30",
                CustomBackgroundOpacity = 1,
            });
            using var background = new GlassBackgroundLayer();
            var result = background.Capture(new PixelRect(0, 0, 8, 4), 8, 4, 5, allowDesktop: true);

            Assert.NotNull(result);
            Assert.Equal(GlassBackdropSource.Custom, result.Source);
            // The panel must show the user's colour, not the capture fallback body.
            Assert.Equal(new byte[] { 0x30, 0x3B, 0xFF, 0xFF }, result.Pixels[..4]);
            // No native capture session may be started for a flat colour.
            dynamic performance = background.InspectPerformance();
            Assert.Equal(0, (int)performance.live.captureAcquisitions);
        });
    }

    [Fact]
    public void ZeroOpacityStaysClickableBecauseThePanelKeepsOneAlphaStep()
    {
        StaTest.Run(() =>
        {
            using var theme = ThemeService.Initialize(System.Windows.Threading.Dispatcher.CurrentDispatcher);
            theme.Update(new AppearanceSettings
            {
                Theme = ThemeMode.Dark,
                UseCustomBackground = true,
                CustomBackgroundColor = "#000000",
                CustomBackgroundOpacity = 0,
            });
            using var surface = new LiquidGlassSurface { ReadBackdropScene = () => new() };
            surface.SetPanelShape(new Rect(0, 0, 200, 100), 20);
            surface.Measure(new Size(200, 100));
            surface.Arrange(new Rect(0, 0, 200, 100));
            var rendered = new RenderTargetBitmap(200, 100, 96, 96, PixelFormats.Pbgra32);
            rendered.Render(surface);
            var pixels = new byte[200 * 100 * 4];
            rendered.CopyPixels(pixels, 800, 0);

            // alpha 0 would make the layered window click-through: icons could not be hit.
            Assert.InRange(pixels[(50 * 200 + 100) * 4 + 3], 1, 3);
        });
    }

    [Fact]
    public void CustomBackgroundSurvivesSavingAndReloadingSettings()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-custom-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new WidgeDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Appearance.UseCustomBackground = true;
            data.Settings.Current.Appearance.CustomBackgroundColor = "#1E90FF";
            data.Settings.Current.Appearance.CustomBackgroundOpacity = .4;
            data.Settings.Save();

            var reloaded = new WidgeDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Assert.True(reloaded.Settings.Current.Appearance.UseCustomBackground);
            Assert.Equal("#1E90FF", reloaded.Settings.Current.Appearance.CustomBackgroundColor);
            Assert.Equal(.4, reloaded.Settings.Current.Appearance.CustomBackgroundOpacity);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void AMalformedColourFallsBackInsteadOfLeavingThePanelUnpainted()
    {
        var root = Path.Combine(Path.GetTempPath(), "WidgeDesk-colour-" + Guid.NewGuid().ToString("N"));
        try
        {
            var data = new WidgeDeskDataStore(new AppPaths(root));
            data.LoadAll();
            data.Settings.Current.Appearance.CustomBackgroundColor = "not-a-colour";
            data.Settings.Current.Appearance.CustomBackgroundOpacity = 5;
            data.Settings.Save();

            var reloaded = new WidgeDeskDataStore(new AppPaths(root));
            reloaded.LoadAll();
            Assert.Equal(AppearanceSettings.DefaultCustomBackgroundColor,
                reloaded.Settings.Current.Appearance.CustomBackgroundColor);
            Assert.Equal(1, reloaded.Settings.Current.Appearance.CustomBackgroundOpacity);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    [Fact]
    public void SettingsPageRejectsAnIncompleteColourBeforeItIsSaved()
    {
        var settings = new WidgeDeskSettings();
        settings.Appearance.CustomBackgroundColor = "#FF";
        Assert.Equal("外观数值超出允许范围。", SettingsPageEditor.Validate(settings));
    }
}
