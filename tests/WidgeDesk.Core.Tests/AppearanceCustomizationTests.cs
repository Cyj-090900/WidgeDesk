using WidgeDesk.Core.Models;
using WidgeDesk.Core.Models.Settings;
using WidgeDesk.Core.Storage;

namespace WidgeDesk.Core.Tests;

public sealed class AppearanceCustomizationTests
{
    [Fact]
    public void LegacyFrostedChoiceLoadsAsLiquidGlassWithoutLosingTransparency()
    {
        using var temp = new TempDataDirectory();
        var store = new SettingsStore(temp.Paths.SettingsFile);
        store.Load();
        store.Current.Appearance.GlassMaterial = (GlassMaterialMode)2;
        store.Current.Appearance.DockGlassTransparency = .75;
        store.Save();
        store.Load();
        // 磨砂已下线：旧文件仍要能读出来，但必须归一成液态玻璃，而不是报「配置损坏」。
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass, store.Current.Appearance.GlassMaterial);
        Assert.Equal(.75, store.Current.Appearance.DockGlassTransparency);
        Assert.Equal(.2, store.Current.Appearance.LauncherGlassTransparency);
        Assert.Equal(GlassMaterialMode.AndroidLiquidGlass,
            SettingsPageEditor.ResetPage(store.Current, "外观").Appearance.GlassMaterial);
    }
}
