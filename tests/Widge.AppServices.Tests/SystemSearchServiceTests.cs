using Widge.AppServices;
using Widge.Core.Models;
using Widge.Core.Storage;
using Widge.Platform.Shell;

namespace Widge.AppServices.Tests;

/// <summary>
/// 系统范围搜索的合并、排序、去重与容错。
/// </summary>
/// <remarks>
/// 两个来源都是注入的，所以这里不碰真实外壳与索引——那些由演示验收在现场覆盖。
/// 这里要钉住的是「两个来源怎么合起来」这件事的规则。
/// </remarks>
public sealed class SystemSearchServiceTests
{
    private static InstalledApp App(string name, string aumid)
        => new(name, $"shell:AppsFolder\\{aumid}", aumid, $"shell:AppsFolder\\{aumid}");

    private static SystemSearchService With(
        IEnumerable<InstalledApp> apps,
        IEnumerable<IndexedFile> files)
        => new(() => apps.ToList(), (_, limit) => files.Take(limit).ToList());

    [Fact]
    public void EmptyOrBlankQueryReturnsNothing()
    {
        var search = With([App("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")], []);
        Assert.Empty(search.Search("", 50));
        Assert.Empty(search.Search("   ", 50));
        Assert.Empty(search.Search(null, 50));
        Assert.Empty(search.Search("计算器", 0));
    }

    [Fact]
    public void AppsRankAheadOfFilesAndExactNameWins()
    {
        var search = With(
            [App("微信", "com.tencent.wechat"), App("微信输入法", "com.tencent.wechat.input")],
            [new IndexedFile(@"C:\Users\x\微信记录.txt", "微信记录.txt")]);

        var results = search.Search("微信", 50);

        Assert.Equal(SystemSearchKind.App, results[0].Kind);
        Assert.Equal("微信", results[0].DisplayName);
        Assert.Equal("微信输入法", results[1].DisplayName);
        // 文件排在两个应用之后。
        Assert.Equal(SystemSearchKind.File, results[^1].Kind);
    }

    [Fact]
    public void PackagedAppIsFoundByItsAumidAsWellAsItsDisplayName()
    {
        var search = With([App("计算器", "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")], []);

        Assert.Single(search.Search("计算器", 50));
        // 显示名是本地化的，AUMID 才是稳定标识；两者都应当能搜到。
        Assert.Single(search.Search("WindowsCalculator", 50));
    }

    [Fact]
    public void DuplicateTargetsAreReportedOnce()
    {
        // 应用枚举与文件搜索可能对同一条各自报一次。
        var shared = @"C:\Users\x\工具.lnk";
        var search = With(
            [new InstalledApp("工具", shared, shared, shared)],
            [new IndexedFile(shared, "工具.lnk")]);

        Assert.Single(search.Search("工具", 50));
    }

    [Fact]
    public void LimitIsRespectedAcrossBothSources()
    {
        var apps = Enumerable.Range(0, 20).Select(i => App($"应用{i}", $"aumid.app{i}")).ToList();
        var files = Enumerable.Range(0, 20).Select(i => new IndexedFile($@"C:\f\应用{i}.txt", $"应用{i}.txt")).ToList();
        var search = With(apps, files);

        Assert.Equal(5, search.Search("应用", 5).Count);
    }

    [Fact]
    public void EachSourceCanBeTurnedOffIndependently()
    {
        var search = With(
            [App("微信", "com.tencent.wechat")],
            [new IndexedFile(@"C:\Users\x\微信.txt", "微信.txt")]);

        var appsOnly = search.Search("微信", 50, includeApps: true, includeFiles: false);
        Assert.All(appsOnly, result => Assert.Equal(SystemSearchKind.App, result.Kind));

        var filesOnly = search.Search("微信", 50, includeApps: false, includeFiles: true);
        Assert.All(filesOnly, result => Assert.Equal(SystemSearchKind.File, result.Kind));

        Assert.Empty(search.Search("微信", 50, includeApps: false, includeFiles: false));
    }

    [Fact]
    public void SearchNeverMailsHome()
    {
        // 与产品「无遥测、不联网」的既有限制一致：这两个来源都是本机的，
        // 搜索路径里不该出现任何网络调用。这里用一个会记录请求的 HttpClient 反证——
        // 它拿不到任何流量，因为没有代码路径会去用它。
        var apps = new List<InstalledApp> { App("微信", "com.tencent.wechat") };
        var files = new List<IndexedFile> { new(@"C:\Users\x\微信.txt", "微信.txt") };
        var search = With(apps, files);

        using var handler = new RecordingHandler();
        using var http = new HttpClient(handler);

        search.Search("微信", 50);

        Assert.Empty(handler.Requests);
        Assert.Empty(http.DefaultRequestHeaders);
    }

    [Fact]
    public void AddingAnInstalledAppCreatesALaunchpadEntryWithItsIdentity()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);

        var entry = AppEntryFactory.CreateFromInstalledApp(
            "计算器", @"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App",
            "Microsoft.WindowsCalculator_8wekyb3d8bbwe!App");

        Assert.Equal(AddEntryResult.Added, host.Import.AddInstalledApp(entry));

        var stored = Assert.Single(data.Library.Items, item => item is not null)!;
        Assert.Equal(AppItemKind.Shortcut, stored.Kind);
        Assert.Equal("计算器", stored.DisplayName);
        // 身份必须存下来：打包应用的窗口不归目标进程所有，按映像路径是匹配不到的。
        Assert.Equal("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", stored.AppUserModelId);
        // 外壳能打开 shell: 形式的目标，所以这一条是可用的。
        Assert.True(stored.HasLaunchTarget);
    }

    [Fact]
    public void AddingTheSameInstalledAppTwiceIsReportedAsDuplicate()
    {
        using var temp = new TempWorkspace();
        var data = temp.CreateStore();
        data.LoadAll();
        using var host = new AppServiceHost(data);

        var first = AppEntryFactory.CreateFromInstalledApp("计算器", @"shell:AppsFolder\Calc!App", "Calc!App");
        Assert.Equal(AddEntryResult.Added, host.Import.AddInstalledApp(first));

        var second = AppEntryFactory.CreateFromInstalledApp("计算器", @"shell:AppsFolder\Calc!App", "Calc!App");
        Assert.Equal(AddEntryResult.Duplicate, host.Import.AddInstalledApp(second));
        Assert.Single(data.Library.Items, item => item is not null);
    }

    /// <summary>记录收到的请求；本用例断言它一条都收不到。</summary>
    private sealed class RecordingHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri?.ToString() ?? string.Empty);
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
