using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using WidgeDesk.UI.Controls;

/// <summary>
/// A live scene made from documented DWM thumbnails. Own-process windows are never
/// registered, so they remain screenshot-visible without recursively sampling the glass.
/// All resources and thumbnail changes belong to the feed's capture STA.
/// </summary>
internal sealed class DocumentedDesktopSceneCapture : IDesktopSceneCapture
{
    private readonly ScreenRect _region;
    private readonly Action<CaptureTrace>? _trace;
    private readonly Func<int[]>? _excludedProcesses;
    private nint _carrier, _owner;
    private HwndSource? _surface;

    // Virtual-screen metrics. GetSystemMetrics(76) is SM_XVIRTUALSCREEN (the left edge of the
    // virtual screen, usually 0), NOT the screen width; using it as a width puts the carrier
    // entirely offscreen, which the Windows 10 capture path rejects.
    private const int SmXvirtualscreen = 76;
    private const int SmYvirtualscreen = 77;
    private const int SmCxvirtualscreen = 78;
    private const int SmCyvirtualscreen = 79;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    /// <summary>Windows 11 首个版本；部分捕获行为在此之前的构建上不同。</summary>
    private const int Windows11Build = 22000;
    private readonly DrawingVisual _background = new();
    private readonly WallpaperBackdrop _wallpaper = new();
    private PersistentWindowCapture? _capture;
    private readonly List<nint> _thumbnails = [];
    private SceneWindow[]? _members;
    private long _barrier;
    internal nint CarrierHandle => _carrier;
    nint IDesktopSceneCapture.CarrierHandle => CarrierHandle;
    public long SceneRevision => _barrier;

    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false, Action<CaptureTrace>? trace = null)
        : this(region, diagnosticOnScreen, trace, null) { }
    internal DocumentedDesktopSceneCapture(ScreenRect region, bool diagnosticOnScreen = false,
        Action<CaptureTrace>? trace = null, Func<int[]>? excludedProcesses = null)
    {
        if (region.Width <= 0 || region.Height <= 0 || (long)region.Width * region.Height > 32_000_000)
            throw new ArgumentOutOfRangeException(nameof(region));
        _region = region; _trace = trace; _excludedProcesses = excludedProcesses;
        try
        {
            // WGC refuses an *owned* popup as a capture item on Windows 10 (E_INVALIDARG from
            // Direct3D11CaptureFramePool.CreateFreeThreaded), while an unowned top-level popup
            // captures fine; Windows 11 accepts both. The owner is what keeps the carrier off
            // the taskbar before its first show, so it is only created where it is safe.
            var usesOwner = WidgeDesk.Platform.WindowsCapabilities.BuildNumber >= Windows11Build;
            if (usesOwner)
            {
                _owner = CreateWindowExW(0, "STATIC", "WidgeDesk scene owner", 0x80000000, 0, 0, 1, 1, 0, 0, 0, 0);
                if (_owner == 0) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
            }
            // A normal redirected popup lets documented thumbnails render to its surface.
            // No TOOLWINDOW, display-affinity exclusion or private composition attributes.
            var (startX, startY) = CaptureStartPosition(region, diagnosticOnScreen);
            _surface = new HwndSource(new HwndSourceParameters("WidgeDesk compatible desktop scene", region.Width, region.Height)
            {
                WindowStyle = unchecked((int)0x80000000), ExtendedWindowStyle = 8,
                ParentWindow = usesOwner ? _owner : 0,
                PositionX = startX,
                PositionY = startY
            });
            _carrier = _surface.Handle; _surface.RootVisual = _background;
            var enabled = 1;
            DwmSetWindowAttribute(_carrier, 11, ref enabled, sizeof(int)); // DISALLOW_PEEK
            DwmSetWindowAttribute(_carrier, 12, ref enabled, sizeof(int)); // EXCLUDED_FROM_PEEK
            ShowWindow(_carrier, 4);
            UpdateWallpaper();
            UpdateExclusions();
            _capture ??= new PersistentWindowCapture(_carrier, trace);
            ParkOffscreen();
            _trace?.Invoke(new("BackdropProvider", "DocumentedThumbnails", Stopwatch.GetTimestamp()));
        }
        catch { Dispose(); throw; }
    }

    /// <summary>
    /// 捕获载体在建立捕获会话之前应当摆放的位置。
    /// </summary>
    /// <remarks>
    /// Windows 10 要求这个窗口与虚拟屏幕有交集。实测：载体完全落在虚拟屏幕之外时，
    /// <c>Direct3D11CaptureFramePool.CreateFreeThreaded</c> 直接抛 <c>E_INVALIDARG</c>；
    /// 只要矩形与虚拟屏幕相交就正常。这里贴着虚拟屏幕右下角、只留 2 像素交集，
    /// 等会话建立后再整体挪出去（见 <see cref="ParkOffscreen"/>）。
    /// </remarks>
    private static (int X, int Y) CaptureStartPosition(ScreenRect region, bool diagnosticOnScreen)
    {
        if (diagnosticOnScreen)
        {
            return (region.X, region.Y);
        }

        // Windows 11 accepts a fully offscreen carrier, and that is what upstream relied on.
        // Windows 10 rejects it, so there the carrier starts with a sliver of overlap and is
        // moved offscreen once the capture session exists (see ParkOffscreen).
        if (WidgeDesk.Platform.WindowsCapabilities.BuildNumber >= Windows11Build)
        {
            return (GetSystemMetrics(SmXvirtualscreen) - region.Width - 64, GetSystemMetrics(SmYvirtualscreen));
        }

        var left = GetSystemMetrics(SmXvirtualscreen);
        var top = GetSystemMetrics(SmYvirtualscreen);
        var width = Math.Max(1, GetSystemMetrics(SmCxvirtualscreen));
        var height = Math.Max(1, GetSystemMetrics(SmCyvirtualscreen));
        return (left + width - 2, top + height - 2);
    }

    /// <summary>
    /// 会话建立之后，把载体整体挪到虚拟屏幕之外，避免它出现在屏幕上。
    /// </summary>
    /// <remarks>
    /// 位置必须在捕获会话建立之后才改：先在屏幕外创建，正是 Windows 10 会拒绝的那一步。
    /// 实测挪出去之后帧照常送达，可见性与可捕获并不冲突。
    ///
    /// 上游原本用 <c>GetSystemMetrics(76)</c> 当屏幕宽度，但索引 76 是
    /// <c>SM_XVIRTUALSCREEN</c>（虚拟屏幕左边界），所以这里改用
    /// <see cref="SmXvirtualscreen"/> 明确定位虚拟屏幕左边界。
    /// </remarks>
    private void ParkOffscreen()
    {
        var x = GetSystemMetrics(SmXvirtualscreen) - _region.Width - 64;
        var y = GetSystemMetrics(SmYvirtualscreen);
        _ = SetWindowPos(_carrier, 0, x, y, _region.Width, _region.Height, SwpNoZOrder | SwpNoActivate);
    }

    internal bool UpdateExclusions()
    {
        UpdateWallpaper();
        var members = Snapshot();
        if (_members is not null && _members.AsSpan().SequenceEqual(members)) return false;
        var next = new List<nint>();
        try
        {
            // EnumWindows is top to bottom. Register bottom to top so newer thumbnails
            // are composited above earlier ones, over the wallpaper surface.
            foreach (var window in members)
            {
                var hr = DwmRegisterThumbnail(_carrier, window.Handle, out var thumbnail);
                if (hr < 0)
                {
                    continue; // A disappearing/unsupported foreign window is not fatal.
                }
                next.Add(thumbnail);
                if (DwmQueryThumbnailSourceSize(thumbnail, out var size) < 0 || size.Width <= 0 || size.Height <= 0)
                {
                    continue;
                }
                var sourceBounds = window.Bounds;
                var left = Math.Max(_region.X, sourceBounds.X);
                var top = Math.Max(_region.Y, sourceBounds.Y);
                var right = Math.Min(_region.X + _region.Width, sourceBounds.X + sourceBounds.Width);
                var bottom = Math.Min(_region.Y + _region.Height, sourceBounds.Y + sourceBounds.Height);
                if (right <= left || bottom <= top) continue;
                var properties = new ThumbnailProperties
                {
                    Flags = 0x1f,
                    Destination = new(left - _region.X, top - _region.Y, right - _region.X, bottom - _region.Y),
                    Source = new(Scale(left - sourceBounds.X, size.Width, sourceBounds.Width),
                        Scale(top - sourceBounds.Y, size.Height, sourceBounds.Height),
                        Scale(right - sourceBounds.X, size.Width, sourceBounds.Width),
                        Scale(bottom - sourceBounds.Y, size.Height, sourceBounds.Height)),
                    Opacity = 255, Visible = 1, ClientOnly = 0
                };
                hr = DwmUpdateThumbnailProperties(thumbnail, ref properties);
                if (hr < 0) _trace?.Invoke(new("Thumbnail", "ForeignWindowUnavailable", Stopwatch.GetTimestamp()));
            }
            foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
            _thumbnails.Clear(); _thumbnails.AddRange(next); next.Clear();
            _members = members;
            _barrier = Stopwatch.GetTimestamp();
            var restart = _capture is not null;
            _capture?.Dispose(); _capture = null;
            Marshal.ThrowExceptionForHR(DwmFlush());
            // A static public thumbnail may emit only its one changed frame. Draining
            // that frame after commit would leave the consumer waiting forever. Start
            // a fresh pool at the committed scene boundary instead.
            if (restart) _capture = new PersistentWindowCapture(_carrier, _trace);
            _trace?.Invoke(new("Scene", "DocumentedSceneChanged", _barrier, BarrierTicks: _barrier));
            return true;
        }
        finally { foreach (var thumbnail in next) DwmUnregisterThumbnail(thumbnail); }
    }

    private static int Scale(int coordinate, int pixels, int extent) => (int)((long)coordinate * pixels / extent);

    private void UpdateWallpaper()
    {
        if (_surface is null || !_wallpaper.Update(new System.Windows.Rect(_region.X, _region.Y, _region.Width, _region.Height),
            _region.Width, _region.Height, 0)) return;
        var pixels = _wallpaper.Pixels;
        if (pixels is null)
        {
            // A removed wallpaper file/COM restart still has a Windows background color.
            // This is an explicit compatibility path; dynamic wallpaper needs the worker.
            var color = GetSysColor(1);
            pixels = new byte[checked(_region.Width * _region.Height * 4)];
            for (var i = 0; i < pixels.Length; i += 4)
            { pixels[i] = (byte)(color >> 16); pixels[i + 1] = (byte)(color >> 8); pixels[i + 2] = (byte)color; pixels[i + 3] = 255; }
        }
        var bitmap = BitmapSource.Create(_region.Width, _region.Height, 96, 96, PixelFormats.Bgra32, null,
            pixels, _region.Width * 4);
        var scale = _surface.CompositionTarget.TransformFromDevice;
        using var drawing = _background.RenderOpen();
        drawing.DrawImage(bitmap, new System.Windows.Rect(0, 0, _region.Width * scale.M11, _region.Height * scale.M22));
    }

    private SceneWindow[] Snapshot()
    {
        var windows = new List<SceneWindow>();
        var excluded = _excludedProcesses?.Invoke() ?? [];
        var shell = GetShellWindow();
        if (shell == 0 || !GetWindowRect(shell, out var desktop))
            throw new InvalidOperationException("Desktop shell is unavailable for compatible capture.");
        EnumWindowProc callback = (hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (hwnd == shell || pid == 0 || pid == Environment.ProcessId || excluded.Contains((int)pid)
                || !IsWindowVisible(hwnd) || IsIconic(hwnd)) return true;
            if (DwmGetWindowAttribute(hwnd, 14, out int cloaked, sizeof(int)) >= 0 && cloaked != 0) return true;
            if (DwmGetWindowAttribute(hwnd, 9, out Rect bounds, Marshal.SizeOf<Rect>()) < 0 && !GetWindowRect(hwnd, out bounds)) return true;
            if (bounds.Right <= bounds.Left || bounds.Bottom <= bounds.Top || bounds.Left >= _region.X + _region.Width
                || bounds.Top >= _region.Y + _region.Height || bounds.Right <= _region.X || bounds.Bottom <= _region.Y) return true;
            windows.Add(new(hwnd, new(bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top)));
            return true;
        };
        if (!EnumWindows(callback, 0)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        windows.Reverse();
        return windows.ToArray();
    }

    internal CapturePixels? TryRead(TimeSpan waitForFrame = default, CancellationToken cancellation = default,
        long sourceNotBeforeTicks = 0, bool reusePixels = false)
    {
        UpdateExclusions();
        var frame = _capture!.TryRead(Math.Max(_barrier, sourceNotBeforeTicks), waitForFrame, cancellation, reusePixels);
        return UpdateExclusions() ? null : frame;
    }
    internal void ReleaseReadbackBuffers() => _capture?.ReleaseReadbackBuffers();
    bool IDesktopSceneCapture.UpdateExclusions() => UpdateExclusions();
    CapturePixels? IDesktopSceneCapture.TryRead(TimeSpan wait, CancellationToken cancellation, long notBefore, bool reuse)
        => TryRead(wait, cancellation, notBefore, reuse);
    void IDesktopSceneCapture.ReleaseReadbackBuffers() => ReleaseReadbackBuffers();

    public void Dispose()
    {
        _capture?.Dispose(); _capture = null;
        foreach (var thumbnail in _thumbnails) DwmUnregisterThumbnail(thumbnail);
        _thumbnails.Clear();
        _surface?.Dispose(); _surface = null; _carrier = 0;
        if (_owner != 0) { DestroyWindow(_owner); _owner = 0; }
    }

    private readonly record struct SceneWindow(nint Handle, ScreenRect Bounds);
    [StructLayout(LayoutKind.Sequential)] private struct Rect(int left, int top, int right, int bottom)
    { public int Left = left, Top = top, Right = right, Bottom = bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct Size { public int Width, Height; }
    [StructLayout(LayoutKind.Sequential)] private struct ThumbnailProperties
    { public uint Flags; public Rect Destination, Source; public byte Opacity; public int Visible, ClientOnly; }
    private delegate bool EnumWindowProc(nint hwnd, nint parameter);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint CreateWindowExW(uint extendedStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string className, [MarshalAs(UnmanagedType.LPWStr)] string name,
        uint style, int x, int y, int width, int height, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetSysColor(int index);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowProc callback, nint parameter);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] private static extern bool IsIconic(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetShellWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUnregisterThumbnail(nint thumbnail);
    [DllImport("dwmapi.dll")] private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);
    [DllImport("dwmapi.dll")] private static extern int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);
}
