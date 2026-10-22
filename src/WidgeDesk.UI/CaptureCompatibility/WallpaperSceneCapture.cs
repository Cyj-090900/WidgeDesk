using System.Diagnostics;
using System.Windows;
using WidgeDesk.UI.Controls;

/// <summary>File/COM wallpaper sampling without D3D/WGC; this still runs in a helper.</summary>
internal sealed class WallpaperSceneCapture(ScreenRect region) : IDesktopSceneCapture
{
    private WallpaperBackdrop _wallpaper = new();
    private CapturePixels? _frame;
    private bool _delivered, _disposed;
    private long _revision;
    public nint CarrierHandle => 0;
    public bool AllowsStaticContent => true;
    // 场景版本只在 UpdateExclusions 里推进。若让它跟着每帧的取样时间走，
    // 消费者在读取期间就会看到版本变化，从而把刚取到的帧当成过期帧丢弃。
    public long SceneRevision => _revision;
    public bool UpdateExclusions()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_wallpaper.Update(new Rect(region.X, region.Y, region.Width, region.Height), region.Width, region.Height, 0)) return false;
        var pixels = _wallpaper.Pixels ?? throw new InvalidOperationException("Wallpaper pixels unavailable.");
        _frame = new(region.Width, region.Height, (byte[])pixels.Clone())
        { SourceQpcTicks = Stopwatch.GetTimestamp(), IsStatic = true, Source = GlassBackdropSource.Wallpaper };
        _revision = _frame.SourceQpcTicks;
        _delivered = false;
        return true;
    }
    public CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        cancellation.ThrowIfCancellationRequested();
        // Resume actually rereads the wallpaper source; it does not relabel old pixels.
        // 重新读取放在这里而不是 UpdateExclusions：喂帧方在读取前刚推进过场景版本，
        // 若此时再改帧时间，隔离层会以「早于恢复边界」为由丢弃这一帧，
        // 而静态壁纸只会产出这一帧，结果就是背景永远拿不到像素。
        if (_frame is not null && _frame.SourceQpcTicks < sourceNotBeforeTicks)
        {
            _wallpaper = new();
            if (_wallpaper.Update(new Rect(region.X, region.Y, region.Width, region.Height),
                region.Width, region.Height, 0) && _wallpaper.Pixels is { } resumed)
            {
                _frame = new(region.Width, region.Height, (byte[])resumed.Clone())
                { SourceQpcTicks = Stopwatch.GetTimestamp(), IsStatic = true, Source = GlassBackdropSource.Wallpaper };
            }
            else _frame = null;
            _delivered = false;
        }
        // 直接调用（诊断与单测）时没有喂帧方先推一次场景，这里补一次首帧。
        // 这条路径只在还没有任何帧时走，不会在读取过程中改写场景版本。
        if (_frame is null) UpdateExclusions();
        if (_delivered || _frame is null) return null;
        _delivered = true; return _frame;
    }
    public void ReleaseReadbackBuffers() { _frame = null; _wallpaper = new(); _delivered = false; }
    public void Dispose() { _disposed = true; _frame = null; }
}
