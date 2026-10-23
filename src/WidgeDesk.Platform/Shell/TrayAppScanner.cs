using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WidgeDesk.Platform.Shell;

/// <summary>通知区里的一枚图标，归纳成「哪个程序在后台运行」。</summary>
/// <param name="ProcessId">图标所属进程。</param>
/// <param name="ExecutablePath">该进程的映像路径；读不到时为 null。</param>
/// <param name="DisplayName">用于显示的名称。</param>
/// <param name="IconWindow">
/// 接收通知消息的窗口。<see cref="CallbackMessage"/> 投递给它，应用就会弹出自己的菜单。
/// </param>
/// <param name="CallbackMessage">图标注册的自定义消息号。</param>
/// <param name="IconId">图标在应用内部的 id，作为该消息的 wParam。</param>
public sealed record TrayApp(int ProcessId, string? ExecutablePath, string DisplayName,
    nint IconWindow = 0, int CallbackMessage = 0, int IconId = 0);

/// <summary>
/// 枚举 Windows 通知区（任务栏右下角展开的那个面板）里的后台应用。
/// </summary>
/// <remarks>
/// <para>
/// <b>为什么需要它。</b>Dock 原有的「运行中应用」来自顶层窗口枚举
/// （<c>WindowLocator.EnumerateTopLevelWindows</c>）。但很多常驻程序——微信、Telegram、
/// Spotify、OneDrive、云盘客户端——平时<b>没有顶层窗口</b>，只留一枚通知区图标。
/// 按窗口枚举永远看不到它们，于是 Dock 上少了这些"正在运行"的应用。
/// </para>
/// <para>
/// <b>怎么做。</b>通知区本质是 explorer 进程里的一条 <c>ToolbarWindow32</c>。
/// 读它的每个按钮需要在 explorer 的地址空间里执行 <c>TB_GETBUTTON</c>，
/// 再把 <c>TRAYDATA</c> 结构读回来——这是跨进程读内存，必须显式分配远端缓冲区。
/// </para>
/// <para>
/// <b>取图标不用远端 HICON。</b>跨进程的图标句柄在本进程里不能直接绘制。
/// 这里只取「图标属于哪个进程」，图标与名称再按映像路径走既有的
/// <see cref="IconExtractor"/> 与文件说明，和 Dock 其它磁贴同一条来源。
/// </para>
/// <para>
/// <b>失败即空。</b>explorer 重启、权限不足、结构体布局对不上，都会让这条路径
/// 拿不到东西。那时返回空集合：Dock 少几枚图标，而不是整体报错。
/// </para>
/// </remarks>
public static class TrayAppScanner
{
    private const int TbButtonCount = 0x0418;
    private const int TbGetButton = 0x0417;

    private const uint ProcessVmOperation = 0x0008;
    private const uint ProcessVmRead = 0x0010;
    private const uint ProcessVmWrite = 0x0020;
    private const uint ProcessQueryLimitedInformation = 0x1000;

    private const uint MemCommit = 0x1000;
    private const uint MemRelease = 0x8000;
    private const uint PageReadWrite = 0x04;

    /// <summary>远端缓冲区的容量。TRAYDATA 结构远小于此，留足余量。</summary>
    private const int RemoteBufferSize = 0x1000;

    /// <summary>x64 上 <c>TBBUTTON</c> 的大小；用它判断读回的字节够不够解析。</summary>
    private const int TbButtonSize = 32;

    /// <summary>
    /// 读取当前通知区里的后台应用。
    /// </summary>
    /// <param name="ownProcessId">本进程 id；它自己的图标不算后台应用。</param>
    /// <remarks>
    /// 同一进程可能挂多枚图标（例如某些程序的通知 + 状态两个），这里按进程去重，
    /// 一个程序在 Dock 上只留一个入口。
    /// </remarks>
    public static IReadOnlyList<TrayApp> Read(uint ownProcessId)
    {
        // 两条工具栏：可见托盘区，以及「^」展开后的溢出面板。
        // 只读前一条会漏掉用户藏起来的图标——它们同样是正在运行的程序。
        var seen = new HashSet<uint>();
        var result = new List<TrayApp>();

        foreach (var toolbar in TrayToolbars.Visible())
        {
            foreach (var app in ReadToolbar(toolbar, ownProcessId))
            {
                if (seen.Add((uint)app.ProcessId)) result.Add(app);
            }
        }

        return result;
    }

    /// <summary>读一条工具栏里的每一个通知图标。</summary>
    private static IEnumerable<TrayApp> ReadToolbar(nint toolbar, uint ownProcessId)
    {
        _ = GetWindowThreadProcessId(toolbar, out var explorerProcessId);
        if (explorerProcessId == 0) yield break;

        var process = OpenProcess(ProcessVmOperation | ProcessVmRead | ProcessVmWrite, false, explorerProcessId);
        if (process == 0) yield break;

        nint remote = 0;
        List<TrayApp>? found = null;
        try
        {
            remote = VirtualAllocEx(process, 0, RemoteBufferSize, MemCommit, PageReadWrite);
            if (remote == 0) yield break;

            var count = (int)SendMessageW(toolbar, TbButtonCount, 0, 0);
            if (count is <= 0 or > 256) yield break;

            var buffer = new byte[RemoteBufferSize];
            found = new List<TrayApp>(count);

            for (var index = 0; index < count; index++)
            {
                if (SendMessageW(toolbar, TbGetButton, index, remote) == 0) continue;
                if (!ReadProcessMemory(process, remote, buffer, buffer.Length, out var read) || read < TbButtonSize) continue;

                // TB_GETBUTTON 写回的是 TBBUTTON，不是 TRAYDATA。
                // 64 位布局：iBitmap(4) idCommand(4) fsState(1) fsStyle(1) bReserved(6)
                //            dwData(8, 偏移 16) iString(8, 偏移 24)。
                // dwData 才是 explorer 地址空间里那块 TRAYDATA 的指针。
                var trayData = (nint)BitConverter.ToInt64(buffer, 16);
                if (trayData == 0) continue;
                if (!ReadProcessMemory(process, trayData, buffer, buffer.Length, out read) || read < 8) continue;

                // TRAYDATA（x64）：hwnd(8) id(4) 消息号(4) hIcon(8) ...
                // 后面三项正是让应用弹出自己菜单所需要的参数。
                var hwnd = (nint)BitConverter.ToInt64(buffer, 0);
                if (hwnd == 0 || !IsWindow(hwnd)) continue;
                var iconId = BitConverter.ToInt32(buffer, 8);
                var message = BitConverter.ToInt32(buffer, 12);

                _ = GetWindowThreadProcessId(hwnd, out var owner);
                if (owner == 0 || owner == ownProcessId) continue;
                if (owner == explorerProcessId) continue; // 系统图标（音量、网络、操作中心）

                found.Add(Describe(owner) with
                {
                    IconWindow = hwnd,
                    CallbackMessage = message,
                    IconId = iconId,
                });
            }
        }
        finally
        {
            if (remote != 0) _ = VirtualFreeEx(process, remote, 0, MemRelease);
            _ = CloseHandle(process);
        }

        // 迭代器里不能 yield catch 块之外的东西，先把结果搬出来再逐个返回。
        if (found is null) yield break;
        foreach (var app in found) yield return app;
    }

    /// <summary>按进程 id 造一条可显示的后台应用记录。</summary>
    private static TrayApp Describe(uint processId)
    {
        var path = TryGetExecutablePath(processId);
        if (string.IsNullOrWhiteSpace(path)) return new TrayApp((int)processId, null, $"进程 {processId}");

        return new TrayApp((int)processId, path, ResolveName(path));
    }

    /// <summary>映像的「文件说明」，与资源管理器里显示的一致；读不到就退回文件名。</summary>
    private static string ResolveName(string path)
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // 只是名字差一点，不影响可用性。
        }

        var name = Path.GetFileNameWithoutExtension(path);
        return string.IsNullOrWhiteSpace(name) ? path : name;
    }

    private static string? TryGetExecutablePath(uint processId)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (handle == 0) return null;
        try
        {
            var buffer = new char[1024];
            uint size = (uint)buffer.Length;
            return QueryFullProcessImageName(handle, 0, buffer, ref size)
                ? new string(buffer, 0, (int)size) : null;
        }
        finally { _ = CloseHandle(handle); }
    }

    /// <summary>
    /// 通知区的两条工具栏：可见托盘区与溢出面板。
    /// </summary>
    /// <remarks>
    /// 溢出面板（<c>NotifyIconOverflowWindow</c>）是独立顶层窗口，里面的
    /// <c>ToolbarWindow32</c> 标题为「溢出通知区域」。它随「^」按钮创建，
    /// 创建后即使收起也仍然存在，因此可以静默读取，不必弹出面板打扰用户。
    /// </remarks>
    private static List<nint> FindTrayToolbars()
    {
        var toolbars = new List<nint>(2);
        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0) return toolbars;
        var notify = FindWindowExW(tray, 0, "TrayNotifyWnd", null);
        if (notify == 0) return toolbars;
        var pager = FindWindowExW(notify, 0, "SysPager", null);
        if (pager != 0)
        {
            var visible = FindWindowExW(pager, 0, "ToolbarWindow32", null);
            if (visible != 0) toolbars.Add(visible);
        }

        var overflow = FindWindowW("NotifyIconOverflowWindow", null);
        if (overflow != 0)
        {
            var hidden = FindWindowExW(overflow, 0, "ToolbarWindow32", null);
            if (hidden != 0) toolbars.Add(hidden);
        }

        return toolbars;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowW(string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint FindWindowExW(nint parent, nint after, string className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SendMessageW(nint hwnd, int message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint hwnd);

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

    [DllImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true,
        CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] name, ref uint size);
}
