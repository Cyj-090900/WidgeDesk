using WidgeDesk.AppServices.Updates;

namespace WidgeDesk.AppServices.Tests;

/// <summary>
/// 静态清单形式的更新元数据（替代 GitHub API，避开匿名限流）。
/// </summary>
/// <remarks>
/// 清单是明文文件，谁都可能改它；因此这一组里「拒绝」的用例比「接受」的更关键。
/// </remarks>
public sealed class UpdateManifestTests
{
    private static readonly Uri Source =
        new("https://raw.githubusercontent.com/Cyj-090900/WidgeDesk/main/latest.json");

    private const string Sha = "8f14e45fceea167a5a36dedd4bea2543f7b5c1b1b8b8b8b8b8b8b8b8b8b8b8b8";

    private static string Manifest(
        string version = "5.1.0",
        string? fileName = null,
        long size = 1024,
        string sha = Sha,
        string page = "https://github.com/Cyj-090900/WidgeDesk/releases/tag/v5.1.0") => $$"""
        {
          "version": "{{version}}",
          "fileName": "{{fileName ?? $"WidgeDesk-{version}-win-x64-Setup.exe"}}",
          "size": {{size}},
          "sha256": "{{sha}}",
          "notes": "更新说明",
          "page": "{{page}}",
          "downloadUrl": "https://github.com/Cyj-090900/WidgeDesk/releases/download/v{{version}}/WidgeDesk-{{version}}-win-x64-Setup.exe"
        }
        """;

    [Fact]
    public void 有效清单给出可下载的新版本()
    {
        var release = UpdateClient.ParseManifest(Manifest(), new Version(5, 0, 1), Source);

        Assert.NotNull(release);
        Assert.Equal(new Version(5, 1, 0), release.Version);
        Assert.Equal("WidgeDesk-5.1.0-win-x64-Setup.exe", release.FileName);
        Assert.Equal(Sha.ToLowerInvariant(), release.Sha256);
        Assert.Equal(1024, release.Size);
        Assert.Equal(
            "https://github.com/Cyj-090900/WidgeDesk/releases/download/v5.1.0/WidgeDesk-5.1.0-win-x64-Setup.exe",
            release.DownloadUrl.AbsoluteUri);
    }

    /// <summary>不比当前版本新就不给更新——与 API 那条路一致。</summary>
    [Theory]
    [InlineData("5.0.1")]
    [InlineData("5.0.0")]
    public void 不提供同级或更旧的版本(string version)
        => Assert.Null(UpdateClient.ParseManifest(Manifest(version), new Version(5, 0, 1), Source));

    /// <summary>文件名的版本必须与清单声明的版本一致。</summary>
    [Fact]
    public void 拒绝文件名与版本不一致的清单()
    {
        var lying = Manifest("5.1.0", fileName: "WidgeDesk-5.0.0-win-x64-Setup.exe");

        Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(lying, new Version(5, 0, 1), Source));
    }

    /// <summary>摘要必须是 64 位十六进制；缺了它就无法校验下载到的包。</summary>
    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("sha256:8f14e45fceea167a5a36dedd4bea2543f7b5c1b1b8b8b8b8b8b8b8b8b8b8b8b8")]
    public void 拒绝无效摘要(string sha)
        => Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(Manifest(sha: sha), new Version(5, 0, 1), Source));

    /// <summary>页面地址必须指向同一版本，否则用户点「详情」会看到别的东西。</summary>
    [Fact]
    public void 拒绝指向别处的发行页()
    {
        var wrong = Manifest(page: "https://github.com/Cyj-090900/WidgeDesk/releases/tag/v5.0.0");

        Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(wrong, new Version(5, 0, 1), Source));
    }

    /// <summary>页面地址不能是别人的仓库——这是「清单被改过」最常见的利用方式。</summary>
    [Fact]
    public void 拒绝外部仓库的发行页()
    {
        var hostile = Manifest(page: "https://github.com/attacker/WidgeDesk/releases/tag/v5.1.0");

        Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(hostile, new Version(5, 0, 1), Source));
    }

    /// <summary>大小必须为正且不超过上限。</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(UpdateClient.MaximumInstallerSize + 1)]
    public void 拒绝无效大小(long size)
        => Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(Manifest(size: size), new Version(5, 0, 1), Source));

    /// <summary>残缺的 JSON 应当是「格式无效」，而不是别的异常类型。</summary>
    [Fact]
    public void 拒绝残缺清单()
        => Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest("""{"version":"5.1.0"}""", new Version(5, 0, 1), Source));

    /// <summary>下载地址指向别人的仓库时必须拒绝——那是清单被改过之后最危险的后果。</summary>
    [Fact]
    public void 拒绝外部仓库的下载地址()
    {
        var hostile = Manifest().Replace(
            "https://github.com/Cyj-090900/WidgeDesk/releases/download",
            "https://github.com/attacker/WidgeDesk/releases/download");

        Assert.Throws<InvalidDataException>(
            () => UpdateClient.ParseManifest(hostile, new Version(5, 0, 1), Source));
    }
}
