using System.Net.NetworkInformation;
using System.Runtime.InteropServices;

namespace WidgeDesk.Platform.Shell;

/// <summary>托盘图标要画哪一种网络形态。</summary>
public enum NetworkKind
{
    /// <summary>没有任何可用连接。</summary>
    Offline = 0,

    /// <summary>有线连接。</summary>
    Wired = 1,

    /// <summary>无线连接。</summary>
    Wireless = 2,
}

/// <summary>一次网络状态取样。</summary>
/// <param name="Kind">连接形态。</param>
/// <param name="Name">适配器显示名，用于提示文本。</param>
/// <param name="IsInternetReady">系统报告存在网络连接。</param>
public sealed record NetworkStatus(NetworkKind Kind, string? Name, bool IsInternetReady);

/// <summary>一次音量状态取样。</summary>
/// <param name="Level">0..1 的主音量。</param>
/// <param name="Muted">是否静音。</param>
public sealed record VolumeStatus(double Level, bool Muted);

/// <summary>
/// 读取托盘要显示的系统状态，并在需要时修改音量。
/// </summary>
/// <remarks>
/// 这里<strong>只读状态，不模拟系统浮层</strong>。实测这台 Windows 10 上，
/// 通过 <c>WM_LBUTTONDOWN</c> 或真实鼠标点击任务栏的 <c>TrayButton</c> 都唤不出操作中心，
/// 系统浮层不接受合成输入。所以 Dock 不复制浮层，而是自己画状态、
/// 点击时打开对应的系统页面或系统音量合成器——那是确实能到达的去处。
///
/// 所有读取都做成失败即返回 <c>null</c>：托盘显示降级为中性图标，
/// 绝不因为音频驱动或网络服务异常把 Dock 拖垮。
/// </remarks>
public static class TrayStatusReader
{
    /// <summary>读取当前网络形态；任何异常都视为离线。</summary>
    public static NetworkStatus ReadNetwork()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network => network.OperationalStatus == OperationalStatus.Up
                    && network.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
                .ToArray();

            if (candidates.Length == 0) return new NetworkStatus(NetworkKind.Offline, null, false);

            // 同时插着网线和 Wi-Fi 时，任务栏显示 Wi-Fi：它才是用户会去点的那一项。
            var wireless = candidates.FirstOrDefault(network =>
                network.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            if (wireless is not null)
                return new NetworkStatus(NetworkKind.Wireless, wireless.Name, NetworkInterface.GetIsNetworkAvailable());

            var wired = candidates.First(network => network.NetworkInterfaceType is
                NetworkInterfaceType.Ethernet or NetworkInterfaceType.GigabitEthernet or NetworkInterfaceType.Ethernet3Megabit);
            return new NetworkStatus(NetworkKind.Wired, wired.Name, NetworkInterface.GetIsNetworkAvailable());
        }
        catch (NetworkInformationException) { return new NetworkStatus(NetworkKind.Offline, null, false); }
        catch (PlatformNotSupportedException) { return new NetworkStatus(NetworkKind.Offline, null, false); }
    }

    /// <summary>读取主音量；没有可用的默认输出设备时返回 <c>null</c>。</summary>
    public static VolumeStatus? ReadVolume()
    {
        var endpoint = TryGetEndpoint();
        if (endpoint is null) return null;
        try
        {
            if (endpoint.GetMasterVolumeLevelScalar(out var level) != 0) return null;
            if (endpoint.GetMute(out var muted) != 0) return null;
            return new VolumeStatus(Math.Clamp(level, 0, 1), muted);
        }
        finally { Release(endpoint); }
    }

    /// <summary>把主音量设为给定值（0..1）。</summary>
    public static bool SetVolume(double level)
    {
        var endpoint = TryGetEndpoint();
        if (endpoint is null) return false;
        try { return endpoint.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), Guid.Empty) == 0; }
        finally { Release(endpoint); }
    }

    /// <summary>切换静音。</summary>
    public static bool SetMuted(bool muted)
    {
        var endpoint = TryGetEndpoint();
        if (endpoint is null) return false;
        try { return endpoint.SetMute(muted, Guid.Empty) == 0; }
        finally { Release(endpoint); }
    }

    /// <summary>打开系统音量合成器。它是真实存在的系统窗口，不是我们画的。</summary>
    public static void OpenVolumeMixer()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "sndvol.exe");
        if (File.Exists(path)) ShellExecutor.Open(path);
    }

    private static void Release(IAudioEndpointVolume endpoint)
    {
        if (Marshal.IsComObject(endpoint)) Marshal.ReleaseComObject(endpoint);
    }

    /// <summary>取默认播放设备的主音量接口；任何一步失败都返回 <c>null</c>。</summary>
    private static IAudioEndpointVolume? TryGetEndpoint()
    {
        object? enumeratorObject = null;
        try
        {
            enumeratorObject = new MMDeviceEnumeratorComObject();
            var enumerator = (IMMDeviceEnumerator)enumeratorObject;
            // eRender = 0，eConsole = 0：与任务栏音量图标用的是同一个默认设备。
            if (enumerator.GetDefaultAudioEndpoint(0, 0, out var device) != 0 || device is null) return null;
            try
            {
                var iid = typeof(IAudioEndpointVolume).GUID;
                // CLSCTX_ALL = 23
                if (device.Activate(ref iid, 23, 0, out var activated) != 0) return null;
                return activated as IAudioEndpointVolume;
            }
            finally { Marshal.ReleaseComObject(device); }
        }
        catch (COMException) { return null; }
        catch (InvalidCastException) { return null; }
        catch (PlatformNotSupportedException) { return null; }
        finally { if (enumeratorObject is not null) Marshal.ReleaseComObject(enumeratorObject); }
    }

    // ---- Core Audio 的最小互操作面 ----

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private sealed class MMDeviceEnumeratorComObject
    {
    }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, int stateMask, out nint devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid interfaceId, int classContext, nint activationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(nint notify);
        int UnregisterControlChangeNotify(nint notify);
        int GetChannelCount(out uint count);
        int SetMasterVolumeLevel(float level, Guid context);
        int SetMasterVolumeLevelScalar(float level, Guid context);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, Guid context);
        int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, Guid context);
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }
}
