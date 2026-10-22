using WidgeDesk.Core.Models;
using WidgeDesk.Core.Storage;
using WidgeDesk.Platform.Shell;

namespace WidgeDesk.AppServices.Tests;

/// <summary>
/// 自动磁贴的归纳规则（D-07 的扩展：正在运行的应用自动出现在 Dock 上）。
/// </summary>
/// <remarks>
/// 全部是对窗口快照的纯函数运算，不需要真的开窗口——归纳规则本身才是要固化下来的东西：
/// 哪些窗口算一个应用、什么情况不生成磁贴、库里已有条目时用它还是用窗口信息。
/// </remarks>
public sealed class RunningAppCatalogTests
{
    private const uint OwnProcess = 9999;

    [Fact]
    public void 正在运行的应用会生成一枚自动磁贴()
    {
        var windows = new[]
        {
            Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe"),
        };

        var apps = Build(windows);

        var app = Assert.Single(apps);
        Assert.Equal("notepad", app.DisplayName);
        Assert.Equal(@"C:\Apps\notepad.exe", app.Target);
        Assert.Equal(new nint[] { 1 }, app.Windows);
    }

    /// <summary>同一个应用的多个窗口归成一枚磁贴，而不是每个窗口一枚。</summary>
    [Fact]
    public void 同一应用的多个窗口只生成一枚磁贴()
    {
        var windows = new[]
        {
            Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe"),
            Window(handle: 2, pid: 100, imagePath: @"C:\Apps\notepad.exe"),
            Window(handle: 3, pid: 100, imagePath: @"C:\Apps\notepad.exe"),
        };

        var app = Assert.Single(Build(windows));

        Assert.Equal(3, app.Windows.Count);
        Assert.Contains((nint)3, app.Windows);
    }

    /// <summary>
    /// 两个 UWP 应用共享同一个宿主进程，必须按身份分开。
    /// </summary>
    /// <remarks>
    /// 按映像路径分组会把它们并成一个——这正是 <c>WindowMatcher</c> 里
    /// 反复强调的那类错误，在磁贴归纳上同样成立。
    /// </remarks>
    [Fact]
    public void 共享宿主进程的两个打包应用按身份分开()
    {
        var windows = new[]
        {
            Window(handle: 1, pid: 500, imagePath: @"C:\Windows\System32\ApplicationFrameHost.exe", aumid: "Contoso.Alpha_1!App"),
            Window(handle: 2, pid: 500, imagePath: @"C:\Windows\System32\ApplicationFrameHost.exe", aumid: "Contoso.Beta_1!App"),
        };

        var apps = Build(windows);

        Assert.Equal(2, apps.Count);
        // 目标必须是 shell: 形式，否则外壳打不开打包应用。
        Assert.Contains(apps, app => app.Target == @"shell:AppsFolder\Contoso.Alpha_1!App");
        Assert.Contains(apps, app => app.Target == @"shell:AppsFolder\Contoso.Beta_1!App");
    }

    /// <summary>已固定的应用不再生成自动磁贴，否则同一应用会有两枚图标。</summary>
    [Fact]
    public void 已固定的应用不再生成自动磁贴()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();

        var entry = AppEntryFactory.CreateFromInstalledApp("记事本", @"C:\Apps\notepad.exe", null!);
        data.Library.Add(entry);

        var windows = new[] { Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe") };

        Assert.Empty(RunningAppCatalog.Build(windows, data.Library,
            new HashSet<string>(StringComparer.Ordinal) { entry.Id },
            [], OwnProcess));
    }

    /// <summary>库里已有条目时沿用它的名称与图标——用户可能改过名字。</summary>
    [Fact]
    public void 库内已有条目时沿用它的名称与图标()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();

        var entry = AppEntryFactory.CreateFromInstalledApp("我的记事本", @"C:\Apps\notepad.exe", null!);
        entry.CustomIconPath = @"C:\Icons\mine.png";
        data.Library.Add(entry);

        var windows = new[] { Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe") };
        var app = Assert.Single(RunningAppCatalog.Build(windows, data.Library, [], [], OwnProcess));

        Assert.Equal("我的记事本", app.DisplayName);
        // 自选图标单独带出来：磁贴加载时它的优先级高于从目标提取的缓存图标。
        Assert.Equal(@"C:\Icons\mine.png", app.CustomIconPath);
        // 记下库条目 id：点击时走「激活已有窗口」，而不是再开一个。
        Assert.Equal(entry.Id, app.EntryId);
    }

    /// <summary>用户移除过的自动项不再出现，否则菜单点了像没反应。</summary>
    [Fact]
    public void 被移除过的自动项不再生成磁贴()
    {
        var windows = new[] { Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe") };
        var suppressed = new[] { RunningAppCatalog.AutoTilePrefix + @"c:\apps\notepad.exe" };

        Assert.Empty(RunningAppCatalog.Build(windows, EmptyLibrary(), [], suppressed, OwnProcess));
    }

    /// <summary>本进程自己的窗口（Dock、启动台）不算「运行中的应用」。</summary>
    [Fact]
    public void 本进程的窗口不算运行中的应用()
    {
        var windows = new[] { Window(handle: 1, pid: OwnProcess, imagePath: @"C:\Apps\widgedesk.exe") };

        Assert.Empty(Build(windows));
    }

    /// <summary>既没有身份也读不到映像路径的窗口无从启动或关闭，不生成磁贴。</summary>
    [Fact]
    public void 无法识别归属的窗口不生成磁贴()
    {
        var windows = new[] { Window(handle: 1, pid: 100, imagePath: null) };

        Assert.Empty(Build(windows));
    }

    [Theory]
    [InlineData("auto:C:\\Apps\\x.exe", true)]
    [InlineData("entry:launcher", false)]
    [InlineData("not-an-id", false)]
    [InlineData(null, false)]
    public void 只有带前缀的id才被当作自动磁贴(string? id, bool expected)
        => Assert.Equal(expected, RunningAppCatalog.IsAutoTile(id));

    /// <summary>同一个应用在两次调和里得到同一个 id——磁贴身份必须稳定。</summary>
    [Fact]
    public void 同一应用的槽位id在多次归纳之间稳定()
    {
        var first = Assert.Single(Build([Window(handle: 1, pid: 100, imagePath: @"C:\Apps\notepad.exe")]));
        var second = Assert.Single(Build([Window(handle: 7, pid: 100, imagePath: @"C:\Apps\notepad.exe")]));

        Assert.Equal(first.Id, second.Id);
    }

    private static IReadOnlyList<RunningApp> Build(IReadOnlyList<WindowIdentityInfo> windows)
        => RunningAppCatalog.Build(windows, EmptyLibrary(), [], [], OwnProcess);

    private static AppLibraryStore EmptyLibrary()
    {
        var store = new AppLibraryStore(Path.Combine(Path.GetTempPath(),
            "WidgeDesk-running-catalog-" + Guid.NewGuid().ToString("N"), "apps.json"));
        store.Load();
        return store;
    }

    private static WindowIdentityInfo Window(nint handle, uint pid, string? imagePath, string? aumid = null)
        => new(handle, "窗口标题", pid, IsMinimized: false, imagePath, WindowAumid: aumid, ProcessAumid: null);
}
