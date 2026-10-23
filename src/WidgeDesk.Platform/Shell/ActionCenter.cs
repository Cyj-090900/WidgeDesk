using System.Runtime.InteropServices;

namespace WidgeDesk.Platform.Shell;

/// <summary>
/// 唤出 Windows 真正的操作中心。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么是 Win+A，而不是直接点任务栏那个按钮。</b>本机实测（Windows 10 19045）：
/// </para>
/// <list type="bullet">
/// <item>向 <c>TrayButton</c> 发送 <c>WM_LBUTTONDOWN/UP</c> 或 <c>BM_CLICK</c>：没有反应。</item>
/// <item>用 <c>SetCursorPos</c> + <c>mouse_event</c> 真实点击它，同样不出面板。</item>
/// <item>用 <c>SendInput</c> 发送 Win+A：<b>操作中心确实弹出</b>（已截图确认）。</item>
/// </list>
/// <para>
/// 也就是说，这个面板只认键盘快捷键这条路径。所以这里复现用户按 Win+A 的效果，
/// 而不是伪造一个"看起来像"的面板。
/// </para>
/// <para>
/// <b>已知边界。</b>Win+A 是切换语义：面板已打开时会把它关掉，这与点任务栏按钮一致。
/// 若用户在系统里改掉了这个快捷键，或组策略禁用了操作中心，这里不会有可见效果——
/// 那属于系统状态，不是可以绕开的实现细节。
/// </para>
/// </remarks>
public static class ActionCenter
{
    private const ushort VkLeftWindows = 0x5B;
    private const ushort VkA = 0x41;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint InputKeyboard = 1;

    /// <summary>按下并松开 Win+A。返回是否把四次输入都投递出去。</summary>
    public static bool Toggle()
    {
        try
        {
            // 顺序不能变：先按下 Win，再按 A，先松开 A，最后松开 Win。
            // 反过来会在系统里留下一个"卡住的 Win 键"，之后所有按键都会被当成组合键。
            return Send(VkLeftWindows, down: true)
                && Send(VkA, down: true)
                && Send(VkA, down: false)
                && Send(VkLeftWindows, down: false);
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    private static bool Send(ushort key, bool down)
    {
        var input = new Input
        {
            Type = InputKeyboard,
            Data = new InputUnion
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    Flags = down ? 0 : KeyEventKeyUp,
                },
            },
        };

        return SendInput(1, [input], Marshal.SizeOf<Input>()) == 1;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // 联合体里只放键盘成员：调用 SendInput 时按 Type 解释，未用到的字节保持 0。
    // 结构体总长必须是 40 字节（x64），否则 SendInput 会整体拒绝。
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KeyboardInput Keyboard;
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public HardwareInput Hardware;
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
    private struct HardwareInput
    {
        public uint Message;
        public ushort ParameterLow;
        public ushort ParameterHigh;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);
}
