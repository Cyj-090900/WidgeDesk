namespace WidgeDesk.Core.Models.Settings;

/// <summary>
/// 外观设置。同时作用于程序坞、启动台与设置中心——
/// <c>ApplyAppearance</c> 应当从同一份来源刷新所有已打开的窗口。
/// </summary>
public sealed class AppearanceSettings
{
    public const double DefaultGlassTintOpacity = .16;
    public const double DefaultGlassBackgroundClarity = .5;
    public const double DefaultDockGlassTransparency = .9;
    public const double DefaultLauncherGlassTransparency = .2;
    public const string DefaultCustomBackgroundColor = "#1E1F24";
    public const double DefaultCustomBackgroundOpacity = 1;

    public double DockGlassTransparency { get; set; } = DefaultDockGlassTransparency;
    public double LauncherGlassTransparency { get; set; } = DefaultLauncherGlassTransparency;

    /// <summary>Legacy shared value retained for older settings files.</summary>
    public double GlassBackgroundClarity { get; set; } = DefaultGlassBackgroundClarity;

    /// <summary>Only the glass body veil; never multiplies refraction, lighting or layer alpha.</summary>
    public double GlassTintOpacity { get; set; } = DefaultGlassTintOpacity;

    /// <summary>
    /// 用自定义纯色替代桌面取样。
    /// </summary>
    /// <remarks>
    /// 桌面取样依赖系统的窗口捕获接口。实测在部分 Windows 10 构建上该接口直接返回
    /// <c>E_INVALIDARG</c>，此时 Dock 与启动台只能退化成一块不受控的兜底色。
    /// 打开这个开关后，面板颜色完全由用户决定，不再受系统能力影响。
    /// </remarks>
    public bool UseCustomBackground { get; set; }

    /// <summary>自定义背景色，格式为 <c>#RRGGBB</c>。</summary>
    public string CustomBackgroundColor { get; set; } = DefaultCustomBackgroundColor;

    /// <summary>
    /// 自定义背景的不透明度，0 为全透明。
    /// </summary>
    /// <remarks>
    /// 全透明时不绘制任何像素，但会保留 1/255 的 alpha：分层窗口按像素 alpha 做命中测试，
    /// 真 0 会让整块面板变成点击穿透，Dock 图标就点不到了。
    /// </remarks>
    public double CustomBackgroundOpacity { get; set; } = DefaultCustomBackgroundOpacity;

    /// <summary>Shared liquid glass material; legacy selections normalize on load and save.</summary>
    public GlassMaterialMode GlassMaterial { get; set; } = GlassMaterialMode.AndroidLiquidGlass;

    /// <summary>
    /// 固定为深色液态玻璃；旧版主题值在加载和保存时统一为深色。
    /// </summary>
    public ThemeMode Theme { get; set; } = ThemeMode.Dark;

    /// <summary>强调色，格式为 <c>#RRGGBB</c>。默认取方案要求的蓝色。</summary>
    public string AccentColor { get; set; } = "#0A84FF";

    /// <summary>
    /// 优先使用 Windows 11 的 DWM 背景材质，而不是自绘的半透明面板。
    /// 在未暴露该 API 的系统版本上忽略；参见 <c>WindowsCapabilities</c>。
    /// </summary>
    public bool UseSystemBackdrop { get; set; } = true;

    /// <summary>减弱或关闭过渡动画。无障碍的基线要求。</summary>
    public bool ReduceMotion { get; set; }

    /// <summary>
    /// 为觉得半透明难以辨读的用户降低面板不透明度，也覆盖系统层面
    /// 关闭了透明效果的情况。
    /// </summary>
    public bool ReduceTransparency { get; set; }

    /// <summary>程序坞的圆角半径，单位为 DIP。</summary>
    public double DockCornerRadius { get; set; } = 40;

    /// <summary>启动台面板的圆角半径，单位为 DIP。</summary>
    public double LauncherCornerRadius { get; set; } = 80;
}
