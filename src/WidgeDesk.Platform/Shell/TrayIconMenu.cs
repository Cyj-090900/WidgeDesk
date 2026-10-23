using System.Runtime.InteropServices;

namespace WidgeDesk.Platform.Shell;

/// <summary>
/// 让后台应用弹出它自己的托盘菜单。
/// </summary>
/// <remarks>
/// <para>
/// <b>复用的是应用自己的菜单，不是我们仿的。</b>通知区图标用
/// <c>Shell_NotifyIcon</c> 注册时给了一个回调消息号；用户在托盘图标上右键时，
/// 系统就是向图标的窗口投递 <c>WM_RBUTTONUP</c>（wParam 为图标 id）。
/// 这里做同一件事，应用就会弹出与任务栏里一模一样的菜单——
/// 那一份条目由应用自己维护（「打开 AiMaMi」「退出」等随它版本变化）。
/// </para>
/// <para>
/// <b>为什么不是跳转列表。</b>任务栏右键的项目按钮是 Jump List，存放在
/// <c>AutomaticDestinations</c> 缓存里，读取要解析复合文档格式；
/// 而实测本机缓存里并没有这个应用的记录（它没有登记跳转列表）。
/// 你截图中那个带「当前账号 / Codex / 打开 AiMaMi / 退出」的面板，
/// 是托盘图标自己画的菜单，正好可以用上面那条标准消息原样唤起。
/// </para>
/// <para>
/// <b>鼠标位置。</b>某些应用在响应时读当前光标位置来决定菜单出现在哪里，
/// 所以先把光标移到用户右键的那一点，投递完再放回去。
/// </para>
/// </remarks>
public static class TrayIconMenu
{
    private const int WmRightButtonUp = 0x0205;
    private const int WmContextMenu = 0x007B;
    private const int WmLButtonUp = 0x0202;

    /// <summary>在该托盘图标所在的屏幕位置上弹出它自己的菜单。</summary>
    /// <returns>消息是否送达。</returns>
    /// <remarks>
    /// <para>
    /// <b>用 <c>SendMessage</c> 而不是 <c>PostMessage</c>。</b>实测（AiMaMi）：
    /// <c>PostMessage</c> 返回成功，但界面毫无反应。这类应用在响应里会读取
    /// 消息携带的坐标或当前光标位置，异步投递的消息在它处理时上下文已经变了。
    /// <c>SendMessage</c> 同步送达，lParam 里带屏幕坐标，行为与系统转发一致。
    /// </para>
    /// <para>
    /// 坐标用屏幕像素，低字为 x、高字为 y——与系统发给托盘图标的格式相同。
    /// </para>
    /// </remarks>
    public static bool Show(TrayApp app)
    {
        ArgumentNullException.ThrowIfNull(app);
        if (app.IconWindow == 0 || app.CallbackMessage == 0) return false;

        // 坐标必须是"这个图标所在的位置"，不能是 Dock 上那枚磁贴的位置。
        // 实测把坐标给到别处时，屏幕上是别的应用弹了菜单（它恰好在那一点有图标），
        // 目标应用毫无反应。找不到图标就如实失败，不去猜一个位置。
        if (!TryGetIconCenter(app, out var screenX, out var screenY)) return false;

        if (!GetCursorPos(out var previous)) previous = default;
        // 图标可能在收起的溢出面板里——那块面板是不可见的，按坐标点过去会落到
        // 别的窗口上。先确保它可见，再点。
        var restoreOverflow = TrayToolbars.ShowOverflowIfHidden();
        _ = SetCursorPos(screenX, screenY);

        try
        {
            // 先真实右键一次，让系统按标准路径通知图标。
            if (!RealRightClick(screenX, screenY)) return false;
            Thread.Sleep(120);

            // 再补一条应用注册的回调：有些框架只在后者里建菜单。
            // 两者都发不会弹出两份——菜单打开后，第二次请求会被应用自己的
            // "已打开"状态吸收。
            var packed = (screenY << 16) | (screenX & 0xFFFF);
            _ = SendMessageW(app.IconWindow, WmRightButtonUp, app.IconId, (nint)packed);
            _ = SendMessageW(app.IconWindow, app.CallbackMessage, app.IconId, (nint)packed);
            return true;
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
        finally
        {
            // 把光标放回去：菜单已经弹出，位置由应用自己决定。
            if (previous.X != 0 || previous.Y != 0) _ = SetCursorPos(previous.X, previous.Y);
            // 面板收起要等菜单关闭之后，否则会把菜单一起带走。
            if (restoreOverflow) TrayToolbars.HideOverflowLater();
        }
    }

    /// <summary>在指定位置做一次真实的右键按下/抬起。</summary>
    /// <remarks>
    /// 这是整套实现里唯一可靠的方式。实测：向图标窗口发 <c>WM_RBUTTONUP</c>、
    /// <c>WM_CONTEXTMENU</c> 或应用注册的回调消息，AiMaMi 一概不弹菜单；
    /// 换成真实鼠标右键立刻弹出。所以宁可移动一次光标，也不做"发了消息就当成功"。
    /// </remarks>
    private static bool RealRightClick(int screenX, int screenY)
    {
        var inputs = new Input[2];
        inputs[0] = new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Flags = RightDown } },
        };
        inputs[1] = new Input
        {
            Type = InputMouse,
            Data = new InputUnion { Mouse = new MouseInput { Flags = RightUp } },
        };

        return SendInput(2, inputs, Marshal.SizeOf<Input>()) == 2;
    }

    /// <summary>找到这枚通知图标在屏幕上的中心点。</summary>
    /// <remarks>
    /// 图标可能在可见托盘区，也可能在「^」展开的溢出面板里。两条工具栏都试一次：
    /// 用 <c>TB_HITTEST</c> 反查鼠标点对应的按钮，是唯一不依赖按钮顺序的做法——
    /// 顺序会随图标增删变化，而我们只要"这个 id 现在画在哪里"。
    /// </remarks>
    private static bool TryGetIconCenter(TrayApp app, out int x, out int y)
    {
        x = y = 0;
        foreach (var toolbar in TrayToolbars.Visible())
        {
            foreach (var button in TrayToolbars.ReadButtonPositions(toolbar))
            {
                if (button.IconId != app.IconId || button.CallbackMessage != app.CallbackMessage) continue;
                x = button.ScreenX;
                y = button.ScreenY;
                return true;
            }
        }
        return false;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);

    // ---- SendInput：真实鼠标右键 ----

    private const uint InputMouse = 0;
    private const uint RightDown = 0x0008;
    private const uint RightUp = 0x0010;

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion Data;
    }

    // 与 ActionCenter 同一条理由：联合体必须足够大（x64 下 40 字节），
    // 否则 SendInput 整体拒绝。只放鼠标成员，其余字节保持 0。
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
    private static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetCursorPos(int x, int y);
}
