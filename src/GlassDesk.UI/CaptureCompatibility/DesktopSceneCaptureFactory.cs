using GlassDesk.Platform;

/// <summary>本机可用的桌面取样方式。</summary>
internal enum DesktopSceneProvider
{
    /// <summary>
    /// 有文档记载的 DWM 缩略图拼装。Windows 7 起可用，是所有构建的兜底方式。
    /// </summary>
    DocumentedThumbnails,

    /// <summary>
    /// 非公开 DWM 共享视觉。只在 Windows 11 上开放，且始终跑在独立辅助进程里。
    /// </summary>
    IsolatedSharedVisual,
}

internal static class DesktopSceneCaptureFactory
{
    // Version numbers never permit private capture in the presentation process.
    internal static DesktopSceneProvider SelectProvider(int build) => build switch
    {
        // 低于产品下限时不猜：如实拒绝，由上层报告「系统版本不受支持」。
        < 19041 => throw new PlatformNotSupportedException(
            "Desktop capture requires Windows 10 2004 (Build 19041) or later."),

        // Windows 10：非公开共享视觉依赖只有 Windows 11 才有的 DWM 接口，
        // 直接用有文档记载的缩略图方式，而不是先失败一次再回退。
        < 22000 => DesktopSceneProvider.DocumentedThumbnails,

        _ => DesktopSceneProvider.IsolatedSharedVisual,
    };

    internal static IDesktopSceneCapture Create(ScreenRect region, bool diagnosticOnScreen, Action<CaptureTrace>? trace)
    {
        var provider = SelectProvider(WindowsCapabilities.BuildNumber);

        if (provider is DesktopSceneProvider.DocumentedThumbnails)
        {
            // 已经是兜底方式，再往下只剩「静态壁纸」这一档。
            return new RecoveringDesktopSceneCapture(
                region,
                () => new IsolatedDesktopSceneCapture(region, documented: true, trace),
                () => new IsolatedDesktopSceneCapture(region, GlassDesk.UI.Controls.GlassBackdropSource.Wallpaper, trace),
                System.Diagnostics.Stopwatch.GetTimestamp);
        }

        return new RecoveringDesktopSceneCapture(region, trace);
    }
}
