namespace WidgeDesk.Core.Models.Settings;

/// <summary>
/// 用户可配置内容的根，持久化为单个 JSON 文档。
/// </summary>
/// <remarks>
/// 这是我们自己的设置模型。它从不读写参考产品的
/// 配置（MacDock 的 <c>%APPDATA%\MacDock\settings.json</c> 或启动台的
/// localStorage），那些配置保持原样，老程序才能继续工作。
/// </remarks>
public sealed class WidgeDeskSettings
{
    /// <summary>
    /// 磁盘上文档的 schema 版本。只要某次变更无法仅靠
    /// 默认值吸收就递增；旧文件在加载时迁移。
    /// </summary>
    public const int CurrentSchemaVersion = 1;

    public int SchemaVersion { get; set; } = CurrentSchemaVersion;

    public AppearanceSettings Appearance { get; set; } = new();

    public DockSettings Dock { get; set; } = new();

    public LauncherSettings Launcher { get; set; } = new();

    public ShortcutSettings Shortcuts { get; set; } = new();

    public DisplaySettings Display { get; set; } = new();

    public PerformanceSettings Performance { get; set; } = new();

    public DesktopTaskbarSettings DesktopTaskbar { get; set; } = new();

    /// <summary>
    /// 把为 null 的节替换为默认值。反序列化手工编辑过或写入不完整的
    /// 文件时，某个节可能为 null，尽管类型声明并非如此；
    /// 否则每个使用方都得自己做 null 检查。
    /// </summary>
    public void Normalize()
    {
        Appearance ??= new AppearanceSettings();
        Dock ??= new DockSettings();
        Launcher ??= new LauncherSettings();
        Shortcuts ??= new ShortcutSettings();
        Display ??= new DisplaySettings();
        Performance ??= new PerformanceSettings();
        DesktopTaskbar ??= new DesktopTaskbarSettings();
        Dock.AutoHide = false;
        Dock.EdgeRevealEnabled = false;
        Launcher.FullscreenMode = false;
        Shortcuts.HotCornersEnabled = false;
        Shortcuts.MouseSideButtonsEnabled = false;

        // Keep legacy enum values readable, but product settings have one material:
        // 磨砂已下线，旧文件里的 FrostedGlass 在加载时归一到液态玻璃。
        Appearance.Theme = ThemeMode.Dark;
        Appearance.GlassMaterial = GlassMaterialMode.AndroidLiquidGlass;
        Dock.SizeScale = double.IsFinite(Dock.SizeScale)
            ? Math.Clamp(Dock.SizeScale, DockSettings.MinimumSizeScale, DockSettings.MaximumSizeScale) : 1;
        // Settings now owns its opaque surface instead of changing every window.
        Appearance.ReduceTransparency = false;
        // 玻璃透明度不再开放设置：固定回内置默认值。
        // 旧文件里的自定义数值会被忽略，这样界面、代码与文档说的是同一件事。
        Appearance.DockGlassTransparency = AppearanceSettings.DefaultDockGlassTransparency;
        Appearance.LauncherGlassTransparency = AppearanceSettings.DefaultLauncherGlassTransparency;
        Appearance.GlassTintOpacity = double.IsFinite(Appearance.GlassTintOpacity)
            ? Math.Clamp(Appearance.GlassTintOpacity, 0, 1) : AppearanceSettings.DefaultGlassTintOpacity;
        Appearance.GlassBackgroundClarity = double.IsFinite(Appearance.GlassBackgroundClarity)
            ? Math.Clamp(Appearance.GlassBackgroundClarity, 0, 1) : AppearanceSettings.DefaultGlassBackgroundClarity;
        Appearance.CustomBackgroundOpacity = double.IsFinite(Appearance.CustomBackgroundOpacity)
            ? Math.Clamp(Appearance.CustomBackgroundOpacity, 0, 1) : AppearanceSettings.DefaultCustomBackgroundOpacity;
        Appearance.CustomBackgroundColor = NormalizeColor(Appearance.CustomBackgroundColor);

        Shortcuts.Normalize();
        DesktopTaskbar.Normalize();
    }

    /// <summary>
    /// 把颜色收成 <c>#RRGGBB</c>；无法解析时退回默认值。
    /// </summary>
    /// <remarks>
    /// 这个文件允许手工编辑。写坏的色值不应让整个外观失效，
    /// 也不该让 Dock 变成没有颜色的黑块。
    /// </remarks>
    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return AppearanceSettings.DefaultCustomBackgroundColor;
        var text = value.Trim();
        if (text.Length == 9 && text[0] == '#') text = text[3..];
        if (text.Length != 7 || text[0] != '#') return AppearanceSettings.DefaultCustomBackgroundColor;
        for (var i = 1; i < text.Length; i++)
            if (!Uri.IsHexDigit(text[i])) return AppearanceSettings.DefaultCustomBackgroundColor;
        return text.ToUpperInvariant();
    }
}
