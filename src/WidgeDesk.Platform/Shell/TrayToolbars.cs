using System.Runtime.InteropServices;

namespace WidgeDesk.Platform.Shell;

/// <summary>通知区里的一枚图标按钮及其屏幕位置。</summary>
/// <param name="ScreenX">按钮中心 x（屏幕像素）。</param>
/// <param name="ScreenY">按钮中心 y（屏幕像素）。</param>
/// <param name="IconId">图标在应用内部的 id。</param>
/// <param name="CallbackMessage">图标注册的回调消息号。</param>
public readonly record struct TrayButton(int ScreenX, int ScreenY, int IconId, int CallbackMessage);

/// <summary>
/// 通知区的两条工具栏（可见托盘区与溢出面板）以及按钮位置的读取。
/// </summary>
/// <remarks>
/// 单独抽出来是因为有两处需要同一份"图标画在哪里"的事实：
/// 扫描后台应用时要读它是哪个进程，弹出应用菜单时要读它在屏幕上的位置。
/// 两处各写一遍，坐标与顺序的差异迟早会让其中一处出错。
/// </remarks>
internal static class TrayToolbars
{
    private const int TbButtonCount = 0x0418;
    private const int TbGetButton = 0x0417;
    private const int TbGetItemRect = 0x0433;

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;
    private const int RemoteBufferSize = 0x1000;
    private const int TbButtonSize = 32;

    /// <summary>两条工具栏的窗口句柄；找不到的那条直接跳过。</summary>
    internal static List<nint> Visible()
    {
        var toolbars = new List<nint>(2);
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray != 0)
        {
            var notify = FindWindowExW(tray, 0, "TrayNotifyWnd", null);
            var pager = notify == 0 ? 0 : FindWindowExW(notify, 0, "SysPager", null);
            var visible = pager == 0 ? 0 : FindWindowExW(pager, 0, "ToolbarWindow32", null);
            if (visible != 0) toolbars.Add(visible);
        }

        // 溢出面板随「^」按钮创建；创建后即使收起也仍在，可以静默读取。
        var overflow = FindWindowW("NotifyIconOverflowWindow", null);
        if (overflow != 0)
        {
            var hidden = FindWindowExW(overflow, 0, "ToolbarWindow32", null);
            if (hidden != 0) toolbars.Add(hidden);
        }

        return toolbars;
    }

    /// <summary>溢出面板此刻是否可见。</summary>
    internal static bool IsOverflowVisible()
    {
        var overflow = FindWindowW("NotifyIconOverflowWindow", null);
        return overflow != 0 && IsWindowVisible(overflow);
    }

    /// <summary>
    /// 若溢出面板处于收起状态，按「^」把它展开。
    /// </summary>
    /// <returns>是否由本次调用展开（调用方据此决定要不要再收回去）。</returns>
    /// <remarks>
    /// 收起时面板窗口仍然存在，只是不可见；图标坐标也在面板里。
    /// 直接按那个坐标点击，点到的是盖在上面的别的窗口——实测正是这个原因
    /// 导致"坐标对了却没反应"。
    /// </remarks>
    internal static bool ShowOverflowIfHidden()
    {
        if (IsOverflowVisible()) return false;

        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return false;
        var notify = FindWindowExW(tray, 0, "TrayNotifyWnd", null);
        var chevron = notify == 0 ? 0 : FindWindowExW(notify, 0, "Button", null);
        if (chevron == 0) return false;
        if (!GetWindowRect(chevron, out var bounds)) return false;

        var x = (bounds.Left + bounds.Right) / 2;
        var y = (bounds.Top + bounds.Bottom) / 2;
        if (!RealClick(x, y)) return false;

        // 面板有展开动画；等它画完再点图标，否则命中的还是下层窗口。
        Thread.Sleep(350);
        return true;
    }

    /// <summary>延后收起溢出面板，避免把刚弹出的菜单一起带走。</summary>
    /// <remarks>
    /// 菜单是溢出面板的弹出物：向面板发 <c>WM_CLOSE</c> 会把刚弹出的菜单一起关掉。
    /// 所以这里**什么都不做**——用户的下一步（选菜单项或点别处）本来就会让两块一起消失。
    /// 保留这个入口是为了让调用方读起来仍然完整：它表达的是"不收起"这个决定。
    /// </remarks>
    internal static void HideOverflowLater() { }

    /// <summary>真实鼠标左键点击。</summary>
    private static bool RealClick(int screenX, int screenY)
    {
        _ = SetCursorPos(screenX, screenY);
        Thread.Sleep(120);
        var inputs = new Input[2];
        inputs[0] = new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = LeftDown } } };
        inputs[1] = new Input { Type = InputMouse, Data = new InputUnion { Mouse = new MouseInput { Flags = LeftUp } } };
        return SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2;
    }

    private const uint InputMouse = 0;
    private const uint LeftDown = 0x0002;
    private const uint LeftUp = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nint ExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostMessageW(nint hwnd, int message, nint wParam, nint lParam);

    /// <summary>
    /// 读一条工具栏里每个按钮的屏幕中心、图标 id 与回调消息号。
    /// </summary>
    /// <remarks>
    /// <c>TB_GETITEMRECT</c> 返回的是工具栏客户区坐标，必须加上工具栏左上角
    /// 才是屏幕坐标。两步都用同一条工具栏句柄，避免混用不同 DPI 的坐标。
    /// </remarks>
    internal static IEnumerable<TrayButton> ReadButtonPositions(nint toolbar)
    {
        if (!GetWindowRect(toolbar, out var bounds)) yield break;

        _ = GetWindowThreadProcessId(toolbar, out var explorerProcessId);
        if (explorerProcessId == 0) yield break;

        var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, explorerProcessId);
        if (process == 0) yield break;

        nint remote = 0;
        List<TrayButton>? found = null;
        try
        {
            remote = VirtualAllocEx(process, 0, RemoteBufferSize, MemCommit, PageReadWrite);
            if (remote == 0) yield break;

            var count = (int)SendMessageW(toolbar, TbButtonCount, 0, 0);
            if (count is <= 0 or > 256) yield break;

            var buffer = new byte[RemoteBufferSize];
            found = new List<TrayButton>(count);

            for (var index = 0; index < count; index++)
            {
                if (SendMessageW(toolbar, TbGetButton, index, remote) == 0) continue;
                if (!ReadProcessMemory(process, remote, buffer, buffer.Length, out var read) || read < TbButtonSize) continue;

                var trayData = (nint)BitConverter.ToInt64(buffer, 16);
                if (trayData == 0) continue;
                if (!ReadProcessMemory(process, trayData, buffer, buffer.Length, out read) || read < 16) continue;

                var iconId = BitConverter.ToInt32(buffer, 8);
                var message = BitConverter.ToInt32(buffer, 12);

                // TB_GETITEMRECT 的 lParam 必须是**目标进程**里的 RECT 指针。
                // 传本进程地址会返回失败（实测还会让调用方一直等），
                // 所以复用上面那块远端缓冲区：写入 0 再读回。
                var rectBytes = Marshal.SizeOf<NativeRect>();
                if (rectBytes > buffer.Length) continue;
                Array.Clear(buffer, 0, rectBytes);
                if (!WriteProcessMemory(process, remote, buffer, rectBytes, out _)) continue;
                if (SendMessageW(toolbar, TbGetItemRect, index, remote) == 0) continue;
                if (!ReadProcessMemory(process, remote, buffer, buffer.Length, out read) || read < rectBytes) continue;

                var rect = new NativeRect
                {
                    Left = BitConverter.ToInt32(buffer, 0),
                    Top = BitConverter.ToInt32(buffer, 4),
                    Right = BitConverter.ToInt32(buffer, 8),
                    Bottom = BitConverter.ToInt32(buffer, 12),
                };

                var centerX = bounds.Left + (rect.Left + rect.Right) / 2;
                var centerY = bounds.Top + (rect.Top + rect.Bottom) / 2;
                found.Add(new TrayButton(centerX, centerY, iconId, message));
            }
        }
        finally
        {
            if (remote != 0) _ = VirtualFreeEx(process, remote, 0, MemRelease);
            _ = CloseHandle(process);
        }

        if (found is null) yield break;
        foreach (var button in found) yield return button;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    internal static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint VirtualAllocEx(nint process, nint address, nint size, uint type, uint protect);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool VirtualFreeEx(nint process, nint address, nint size, uint type);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(nint process, nint address, [Out] byte[] buffer, nint size, out nint read);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteProcessMemory(nint process, nint address, [In] byte[] buffer, nint size, out nint written);
}
