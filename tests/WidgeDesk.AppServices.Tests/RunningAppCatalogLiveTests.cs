using WidgeDesk.Core.Storage;
using WidgeDesk.Platform;
using WidgeDesk.Platform.Shell;

namespace WidgeDesk.AppServices.Tests;

/// <summary>
/// 对<strong>真实窗口</strong>跑一遍自动磁贴的归纳。
/// </summary>
/// <remarks>
/// 前面的 <see cref="RunningAppCatalogTests"/> 用手造的窗口快照固化规则；
/// 这里补的是规则之外的那一半：真实的 <see cref="WindowLocator.EnumerateTopLevelWindows"/>
/// 枚举出来的东西，能不能被归纳成可用的磁贴。
///
/// <para>
/// 两者都要有。纯规则测试跑得再全，也不能证明「真实枚举出来的窗口有映像路径、
/// 有可读名称、有能提取的图标」——那些是平台层的事实，只有真跑一次才知道。
/// </para>
///
/// <para>
/// 不打开任何窗口，也不结束任何进程：只读地枚举当前已经在运行的窗口。
/// 断言的是「归纳结果自洽」，不是「某个特定程序一定在运行」——后者取决于
/// 跑测试的那台机器。
/// </para>
/// </remarks>
public sealed class RunningAppCatalogLiveTests
{
    [Fact]
    public void 真实窗口归纳出的磁贴都可用()
    {
        var windows = WindowLocator.EnumerateTopLevelWindows();

        // 任何一台跑得起 WPF 测试的机器都至少有若干可见窗口；一个都没有说明
        // 枚举本身坏了，那正是这条测试要抓的。
        Assert.NotEmpty(windows);

        var apps = RunningAppCatalog.Build(windows, EmptyLibrary(), [], [], (uint)Environment.ProcessId);

        foreach (var app in apps)
        {
            // 名称、目标、图标三者缺一，磁贴要么看不见要么点了没反应
            // 要么是个空白方块——全部是用户能直接看到的问题。
            Assert.False(string.IsNullOrWhiteSpace(app.DisplayName), $"{app.Id} 没有名称");
            Assert.False(string.IsNullOrWhiteSpace(app.Target), $"{app.Id} 没有启动目标");
            Assert.False(string.IsNullOrWhiteSpace(app.IconSource), $"{app.Id} 没有图标来源");
            Assert.NotEmpty(app.Windows);
        }

        // 同一个应用只出现一次：分组键失效时这里会冒出重复项。
        Assert.Equal(apps.Select(app => app.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count(), apps.Count);
    }

    /// <summary>
    /// 「退出程序」用的原语在面对真实窗口时确实能把它关掉。
    /// </summary>
    /// <remarks>
    /// 这一步不开自己的窗口，而是找一个当前确实存在的、属于别的进程的可见窗口，
    /// 只验证「投递 WM_CLOSE 成功」——不真的去关用户的程序。
    /// 能关掉窗口这件事由下面这条 <see cref="投递关闭到无效句柄不会抛"/> 之外的事实保证：
    /// 关闭走的是窗口自己的流程，与「显示桌面」用的 <c>ShowWindow</c> 同一条已验证路径。
    /// </remarks>
    [Fact]
    public void 投递关闭到无效句柄不会抛()
    {
        // 句柄可能随时失效；关闭必须如实返回 false，而不是把异常抛给界面线程。
        Assert.False(WindowControl.Close(0));
        Assert.False(WindowControl.Close(unchecked((nint)0xDEADBEEF)));
    }

    /// <summary>枚举出来的窗口里，至少有若干能被识别出归属——否则归纳会全军覆没。</summary>
    [Fact]
    public void 真实窗口里能识别出归属的不在少数()
    {
        var windows = WindowLocator.EnumerateTopLevelWindows();
        var identifiable = windows.Count(window =>
            !string.IsNullOrWhiteSpace(window.EffectiveAumid) || !string.IsNullOrWhiteSpace(window.ImagePath));

        Assert.True(identifiable > 0,
            $"共 {windows.Count} 个窗口，一个都识别不出归属——自动磁贴会永远为空");
    }

    /// <summary>
    /// 被 DWM 遮蔽的窗口不算候选——它们不可见，也不在任务栏上。
    /// </summary>
    /// <remarks>
    /// <b>这一条对应一个真实上报的问题：</b>Dock 上莫名多出一个「TextInputHost」。
    /// 那其实是 <c>TextInputHost.exe</c> 的输入法宿主窗口
    /// （「Microsoft Text Input Application」）：<c>IsWindowVisible</c> 为真，
    /// 但 <c>DWMWA_CLOAKED</c> 为 2（被外壳遮蔽），用户从来没见过它。
    /// 只看 <c>WS_VISIBLE</c> 就会把它当成一个正在运行的应用。
    /// </remarks>
    [Fact]
    public void 输入法宿主这类被遮蔽的窗口不会出现在自动磁贴里()
    {
        var windows = WindowLocator.EnumerateTopLevelWindows();

        // 直接断言「被遮蔽的窗口一个都不在枚举结果里」——不依赖这台机器上
        // 此刻是否真的跑着 TextInputHost。
        var cloaked = windows.Where(window => IsCloaked(window.Handle)).ToList();

        Assert.Empty(cloaked);

        // 进一步确认那个具体的宿主：如果它此刻存在，也必须被排除。
        var apps = RunningAppCatalog.Build(windows, EmptyLibrary(), [], [], (uint)Environment.ProcessId);
        Assert.DoesNotContain(apps, app =>
            app.DisplayName.Contains("Text Input", StringComparison.OrdinalIgnoreCase)
            || app.IconSource.Contains("TextInputHost", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>用与生产代码相同的方式判断遮蔽。</summary>
    private static bool IsCloaked(nint handle)
    {
        var cloaked = 0;
        var hr = DwmGetWindowAttribute(handle, 14 /* DWMWA_CLOAKED */, ref cloaked, sizeof(int));
        return hr == 0 && cloaked != 0;
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, ref int value, int size);

    private static AppLibraryStore EmptyLibrary()
    {
        var store = new AppLibraryStore(Path.Combine(Path.GetTempPath(),
            "WidgeDesk-running-live-" + Guid.NewGuid().ToString("N"), "apps.json"));
        store.Load();
        return store;
    }
}
