namespace WidgeDesk.AppServices.Tests;

/// <summary>
/// 点击一枚自动磁贴时该做什么（「与任务栏一致」的判定）。
/// </summary>
/// <remarks>
/// 这一组直接对应一个真实缺陷：答疑工作台已经开着，在 Dock 上再点一次，
/// 结果**又开了一个**。原因是点击路径在「没有库条目」时直接去启动，
/// 而它手上明明握着那个应用的窗口句柄。
/// </remarks>
public sealed class RunningAppActivationTests
{
    /// <summary>只要还有窗口，就必须前置——这是整个修复的核心。</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(5)]
    [InlineData(99)]
    public void 有窗口时绝不启动新实例(int windows)
        => Assert.NotEqual(RunningAppCatalog.Activation.Launch, RunningAppCatalog.DecideActivation(windows));

    [Fact]
    public void 只有一个窗口时直接前置()
        => Assert.Equal(RunningAppCatalog.Activation.Raise, RunningAppCatalog.DecideActivation(1));

    /// <summary>多个窗口时让用户选，而不是随便挑一个——与任务栏点分组图标一致。</summary>
    [Theory]
    [InlineData(2)]
    [InlineData(7)]
    public void 多个窗口时先让用户选择(int windows)
        => Assert.Equal(RunningAppCatalog.Activation.Choose, RunningAppCatalog.DecideActivation(windows));

    /// <summary>一个窗口都没有才轮到启动——那是应用刚关掉、状态还没调和的竞态兜底。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void 没有可用窗口时才启动(int windows)
        => Assert.Equal(RunningAppCatalog.Activation.Launch, RunningAppCatalog.DecideActivation(windows));

    /// <summary>
    /// 退回启动之前，磁贴必须先记下真实存在的窗口句柄。
    /// </summary>
    /// <remarks>
    /// <b>这就是原缺陷的根因。</b>如果自动磁贴不带上窗口句柄，点击时就没有任何
    /// 可前置的目标，只能去启动——多开一个就成了必然结果。
    /// </remarks>
    [Fact]
    public void 自动磁贴必须带上可前置的窗口句柄()
    {
        var windows = new[]
        {
            new WidgeDesk.Platform.Shell.WindowIdentityInfo(
                4242, "答疑工作台", 1234, false, @"C:\Apps\doubt.exe", null, null),
        };

        var store = new WidgeDesk.Core.Storage.AppLibraryStore(Path.Combine(Path.GetTempPath(),
            "WidgeDesk-activation-" + Guid.NewGuid().ToString("N"), "apps.json"));
        store.Load();

        var app = Assert.Single(RunningAppCatalog.Build(windows, store, [], [], 9999));

        Assert.Equal([(nint)4242], app.Windows);
        // 标题也要带上：多窗口时选择列表要显示它们，否则用户看到一排「窗口 1/2/3」。
        Assert.Equal(["答疑工作台"], app.WindowTitles);
    }
}
