using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using GlassDesk.Platform.Interop;

namespace GlassDesk.Platform.Shell;

/// <summary>一个已安装的应用，与「开始」菜单里看到的那一条对应。</summary>
/// <param name="DisplayName">用户可见的名称，例如「计算器」。</param>
/// <param name="LaunchTarget">
/// 交给外壳打开的目标，打包应用是 <c>shell:AppsFolder\&lt;AUMID&gt;</c>。
/// </param>
/// <param name="Aumid">
/// 应用身份。经典 Win32 程序这里存的是可执行文件路径——外壳就是这么报的，
/// 而 <c>shell:AppsFolder\&lt;路径&gt;</c> 同样能打开它。
/// </param>
/// <param name="IconSource">图标来源，可直接交给 <see cref="IconExtractor.Extract"/>。</param>
public sealed record InstalledApp(string DisplayName, string LaunchTarget, string Aumid, string IconSource);

/// <summary>
/// 枚举本机已安装的应用，来源与「开始」菜单相同（外壳的「应用」文件夹）。
/// </summary>
/// <remarks>
/// <b>为什么不用 <c>.lnk</c> 扫描。</b>「开始」菜单的快捷方式只覆盖经典程序的一部分；
/// 打包应用（Store / MSIX）大多没有可直接解析出目标的快捷方式。外壳的应用文件夹
/// 就是「开始」菜单自己列举的东西，用它才拿到与系统一致的集合。
///
/// <para>
/// <b>为什么不用 <c>Shell.Application</c> 的 late binding。</b>那需要 IDispatch，
/// 而本程序集刻意不启用内置 COM 互操作，走的是源生成的 COM 接口。
/// 这里改用 <c>SHCreateItemFromParsingName</c> 取到文件夹项，再
/// <c>BindToHandler(BHID_EnumItems)</c> 拿 <c>IEnumShellItems</c> 逐个走。
/// </para>
///
/// <para>
/// <b>名称必须按键名显式取回。</b>实测对应用文件夹的子项，直接读名称得到的是 AUMID
/// （<c>Anysphere.Cursor</c>），只有先 <c>GetPropertyStoreForKeys</c> 打开按
/// <c>System.ItemNameDisplay</c> 指定的存储，才能拿到用户看到的名字（<c>Cursor</c>）。
/// </para>
///
/// <para>
/// 枚举失败时返回空列表而不是抛异常：调用方应当降级为「只搜文件」，
/// 而不是让整条搜索路径失败。
/// </para>
/// </remarks>
public static class InstalledAppScanner
{
    /// <summary>一个进程内共享的包装器。理由见 <see cref="ShortcutResolver"/> 同名字段的注释。</summary>
    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>外壳的「应用」命名空间，也就是「开始」菜单列举应用的地方。</summary>
    private const string AppsFolderPath = "shell:AppsFolder";

    /// <summary><c>SIGDN_DESKTOPABSOLUTEPARSING</c>：可再次交给外壳解析的稳定名字。</summary>
    private const uint SigdnDesktopAbsoluteParsing = 0x80018001;

    /// <summary>枚举上限，避免一次搜索无上限地枚举下去。</summary>
    private const int MaxApps = 2000;

    /// <summary>枚举已安装应用；任何失败都返回空列表。</summary>
    public static IReadOnlyList<InstalledApp> Scan()
    {
        var apps = new List<InstalledApp>();

        try
        {
            var hr = Shell32.SHCreateItemFromParsingName(AppsFolderPath, 0, in ShellComIds.ShellItem, out var folderPointer);
            if (hr != Ole32.SoK || folderPointer == 0)
            {
                return apps;
            }

            IShellItem? folder = null;

            try
            {
                folder = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(folderPointer, CreateObjectFlags.UniqueInstance);

                hr = folder.BindToHandler(0, in ShellComIds.EnumItems, in ShellComIds.EnumShellItems, out var enumPointer);
                if (hr != Ole32.SoK || enumPointer == 0)
                {
                    return apps;
                }

                IEnumShellItems? enumerator = null;

                try
                {
                    enumerator = (IEnumShellItems)Wrappers.GetOrCreateObjectForComInstance(
                        enumPointer, CreateObjectFlags.UniqueInstance);
                    Collect(enumerator, apps);
                }
                finally
                {
                    if (enumerator is IDisposable disposableEnumerator)
                    {
                        disposableEnumerator.Dispose();
                    }
                }
            }
            finally
            {
                if (folder is IDisposable disposableFolder)
                {
                    disposableFolder.Dispose();
                }

                _ = Marshal.Release(folderPointer);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // 外壳不可用、COM 被拒绝、枚举中途出错……都归为「这次枚举没有结果」。
            // 已经收集到的条目保留在 apps 里。
            _ = ex;
        }

        return apps;
    }

    private static void Collect(IEnumShellItems enumerator, List<InstalledApp> into)
    {
        while (into.Count < MaxApps)
        {
            var hr = enumerator.Next(1, out var childPointer, out var fetched);
            if (hr != Ole32.SoK || fetched == 0 || childPointer == 0)
            {
                return;
            }

            IShellItem? child = null;

            try
            {
                child = (IShellItem)Wrappers.GetOrCreateObjectForComInstance(childPointer, CreateObjectFlags.UniqueInstance);

                var parsingName = ReadDisplayName(child, SigdnDesktopAbsoluteParsing);
                if (string.IsNullOrWhiteSpace(parsingName))
                {
                    continue;
                }

                var item2 = TryGetShellItem2(childPointer);

                try
                {
                    var displayName = item2 is null ? null : ReadStringProperty(item2, in PropertyKeys.ItemNameDisplay);
                    var aumid = item2 is null ? null : ReadStringProperty(item2, in PropertyKeys.AppUserModelId);

                    into.Add(new InstalledApp(
                        (displayName ?? parsingName).Trim(),
                        LaunchTargetFor(parsingName),
                        (aumid ?? parsingName).Trim(),
                        LaunchTargetFor(parsingName)));
                }
                finally
                {
                    if (item2 is IDisposable disposableItem2)
                    {
                        disposableItem2.Dispose();
                    }
                }
            }
            finally
            {
                if (child is IDisposable disposableChild)
                {
                    disposableChild.Dispose();
                }

                _ = Marshal.Release(childPointer);
            }
        }
    }

    /// <summary>把 <c>IShellItem</c> 转成 <c>IShellItem2</c>；不支持时返回 null。</summary>
    private static IShellItem2? TryGetShellItem2(nint childPointer)
    {
        nint item2Pointer = 0;

        try
        {
            // 源生成的接口包装不能直接 QueryInterface，必须在原始指针上做，
            // 与 ShortcutResolver 查询 IPersistFile 的做法一致。
            var hr = Marshal.QueryInterface(childPointer, in ShellComIds.ShellItem2, out item2Pointer);
            if (hr != Ole32.SoK || item2Pointer == 0)
            {
                return null;
            }

            return (IShellItem2)Wrappers.GetOrCreateObjectForComInstance(item2Pointer, CreateObjectFlags.UniqueInstance);
        }
        finally
        {
            if (item2Pointer != 0)
            {
                _ = Marshal.Release(item2Pointer);
            }
        }
    }

    /// <summary>
    /// 外壳要打开的目标。
    /// </summary>
    /// <remarks>
    /// 应用文件夹里既有打包应用（名字是 AUMID）也有经典程序（名字是文件路径），
    /// 两者都通过 <c>shell:AppsFolder\</c> 前缀交给外壳——打包应用拿裸路径去
    /// <c>ShellExecute</c> 是打不开的。
    /// </remarks>
    private static string LaunchTargetFor(string parsingName)
        => parsingName.StartsWith(AppsFolderPath + "\\", StringComparison.OrdinalIgnoreCase)
            ? parsingName
            : AppsFolderPath + "\\" + parsingName;

    private static string? ReadDisplayName(IShellItem item, uint nameType)
    {
        nint pointer = 0;

        try
        {
            return item.GetDisplayName(nameType, out pointer) == Ole32.SoK && pointer != 0
                ? Marshal.PtrToStringUni(pointer)
                : null;
        }
        finally
        {
            if (pointer != 0)
            {
                Ole32.CoTaskMemFree(pointer);
            }
        }
    }

    /// <summary>
    /// 按键名读一个字符串属性。
    /// </summary>
    /// <remarks>
    /// 必须走 <c>GetPropertyStoreForKeys</c>：默认的属性存储里没有这些项（见类注释）。
    /// 读出来的值由属性分配器给出，交给
    /// <see cref="WindowIdentity.ReadStringProperty"/> 统一释放。
    /// </remarks>
    private static string? ReadStringProperty(IShellItem2 item, in PropertyKey key)
    {
        var hr = item.GetPropertyStoreForKeys(in key, 1, 0, in ShellComIds.PropertyStore, out var storePointer);
        if (hr != Ole32.SoK || storePointer == 0)
        {
            return null;
        }

        IPropertyStore? store = null;

        try
        {
            store = (IPropertyStore)Wrappers.GetOrCreateObjectForComInstance(storePointer, CreateObjectFlags.UniqueInstance);
            return WindowIdentity.ReadStringProperty(store, in key);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = ex;
            return null;
        }
        finally
        {
            if (store is IDisposable disposableStore)
            {
                disposableStore.Dispose();
            }

            _ = Marshal.Release(storePointer);
        }
    }
}
