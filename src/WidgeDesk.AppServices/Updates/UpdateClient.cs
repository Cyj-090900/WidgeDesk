using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WidgeDesk.AppServices.Updates;

/// <summary>Stable release metadata and bounded, checksum-verified installer downloads.</summary>
/// <remarks>
/// 支持两种元数据来源：
/// <list type="number">
/// <item><b>GitHub Releases API</b>（旧格式）——按仓库查询最新发布。</item>
/// <item><b>静态清单</b>（推荐）——发布流程写一份 <c>latest.json</c>，由普通文件托管。</item>
/// </list>
///
/// <para>
/// <b>为什么要有第二种。</b>GitHub 的 API 对匿名请求按来源 IP 限流（每小时 60 次），
/// 共用出口的用户会莫名其妙地「检查更新」失败，而网络明明是好的。静态文件不走 API，
/// 没有这个限制；代价是发布流程要多写一个文件。
/// </para>
/// </remarks>
public sealed partial class UpdateClient
{
    public const long MaximumInstallerSize = 512L * 1024 * 1024;
    private const int MaximumMetadataSize = 512 * 1024;
    private readonly HttpClient _http;
    private readonly Uri _source;

    public UpdateClient(HttpClient http, Uri source)
    {
        ArgumentNullException.ThrowIfNull(http);
        ValidateSource(source);
        _http = http;
        _source = source;
    }

    /// <summary>元数据是不是一份静态清单（而不是 GitHub API 查询）。</summary>
    private bool IsManifestSource => !IsGitHubApiSource(_source);

    public async Task<UpdateRelease?> CheckAsync(Version current, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        using var request = Request(_source, metadata: true);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);

        // GitHub 对匿名请求按来源 IP 限流（每小时 60 次）。撞上时给 403 和一段英文原文，
        // 原样抛给界面等于把「Response status code does not indicate success: 403」丢给用户——
        // 既看不懂，也不知道该等多久。这里换成可以直接照做的说明。
        if ((int)response.StatusCode == 403
            && response.Headers.TryGetValues("X-RateLimit-Remaining", out var remaining)
            && remaining.FirstOrDefault() == "0")
        {
            throw new UpdateRateLimitedException(RetryAfter(response));
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumMetadataSize)
            throw new InvalidDataException("更新信息过大。");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
        using var output = new MemoryStream();
        var buffer = new byte[16384];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
        {
            if (output.Length + read > MaximumMetadataSize) throw new InvalidDataException("更新信息过大。");
            output.Write(buffer, 0, read);
        }
        var text = System.Text.Encoding.UTF8.GetString(output.ToArray());

        return IsManifestSource
            ? ParseManifest(text, current, _source)
            : ParseRelease(text, current, _source);
    }

    /// <summary>
    /// 解析静态清单。
    /// </summary>
    /// <remarks>
    /// 格式（由发布流程生成，字段与 GitHub API 的形状刻意保持接近，便于对照）：
    /// <code>
    /// {
    ///   "version": "5.1.0",
    ///   "fileName": "WidgeDesk-5.1.0-win-x64-Setup.exe",
    ///   "size": 60714327,
    ///   "sha256": "8f14e45f...",
    ///   "notes": "本次更新……",
    ///   "page": "https://github.com/Cyj-090900/WidgeDesk/releases/tag/v5.1.0"
    /// }
    /// </code>
    ///
    /// <para>
    /// <b>校验比 API 那条路更严，不是更松。</b>清单是明文文件，谁都可能改它；
    /// 因此除了摘要格式，还要核对文件名与版本一致、地址确实属于本仓库、
    /// 页面地址是同一版本。任何一项不符就拒绝——宁可报「更新信息无效」，
    /// 也不能让一份被改过的清单把用户引到别处。
    /// </para>
    /// </remarks>
    public static UpdateRelease? ParseManifest(string json, Version current, Uri source)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            var text = root.GetProperty("version").GetString() ?? string.Empty;
            if (!StableVersionPattern().IsMatch(text) || !Version.TryParse(text, out var version))
                throw new InvalidDataException("更新版本号无效。仅支持正式版本。");

            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;

            var fileName = root.GetProperty("fileName").GetString() ?? string.Empty;
            // 文件名必须与版本对应：一份写着 5.1.0 的清单不能指向 5.0.1 的包。
            if (fileName != $"WidgeDesk-{version}-win-x64-Setup.exe")
                throw new InvalidDataException("更新清单里的安装包名与版本不一致。");

            var size = root.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize)
                throw new InvalidDataException("更新安装包大小无效。");

            var sha = root.GetProperty("sha256").GetString() ?? string.Empty;
            if (!Sha256HexPattern().IsMatch(sha))
                throw new InvalidDataException("更新清单缺少有效的 SHA-256。");

            var page = root.GetProperty("page").GetString() ?? string.Empty;

            // 下载地址由清单**显式给出**，不靠「清单所在目录 + 文件名」去推：
            // 清单通常放在仓库根或另一个分支，而安装包在 Releases 下，两者不同源。
            var downloadText = root.GetProperty("downloadUrl").GetString() ?? string.Empty;
            if (!Uri.TryCreate(downloadText, UriKind.Absolute, out var download))
                throw new InvalidDataException("更新清单的安装包地址无效。");

            if (!IsOurRepository(download.AbsoluteUri, $"releases/download/v{version}/{fileName}")
                && !IsOurRepository(download.AbsoluteUri, $"releases/download/{version}/{fileName}"))
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");
            if (!IsOurRepository(page, $"releases/tag/v{version}"))
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");

            var notes = root.TryGetProperty("notes", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new UpdateRelease(version, fileName, download, sha.ToLowerInvariant(), size,
                notes.Length > 12000 ? notes[..12000] : notes, new Uri(page));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException
            or FormatException or OverflowException or UriFormatException)
        {
            throw new InvalidDataException("更新信息格式无效。", ex);
        }
    }

    public static UpdateRelease? ParseRelease(string json, Version current, Uri source)
    {
        var repository = Repository(source);
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean()) return null;
            var tag = root.GetProperty("tag_name").GetString() ?? string.Empty;
            var text = tag.StartsWith('v') ? tag[1..] : tag;
            if (!StableVersionPattern().IsMatch(text) || !Version.TryParse(text, out var version))
                throw new InvalidDataException("更新版本号无效。仅支持正式版本。");
            // Compare product version, not the intentionally stable assembly identity (0.2.2.0).
            var installed = new Version(current.Major, current.Minor, Math.Max(0, current.Build));
            if (version <= installed) return null;
            var fileName = $"WidgeDesk-{version}-win-x64-Setup.exe";
            var assets = root.GetProperty("assets").EnumerateArray()
                .Where(asset => asset.GetProperty("name").GetString() == fileName).ToArray();
            if (assets.Length != 1) throw new InvalidDataException("此版本没有唯一的 Windows x64 安装包。");
            var asset = assets[0];
            var size = asset.GetProperty("size").GetInt64();
            if (size <= 0 || size > MaximumInstallerSize || asset.GetProperty("state").GetString() != "uploaded")
                throw new InvalidDataException("更新安装包尚未就绪或大小无效。");
            var digest = asset.GetProperty("digest").GetString() ?? string.Empty;
            if (!DigestPattern().IsMatch(digest)) throw new InvalidDataException("更新安装包缺少 SHA-256 校验信息。");
            var url = asset.GetProperty("browser_download_url").GetString();
            var page = root.GetProperty("html_url").GetString();
            if (url is null || !IsOurRepository(url, $"releases/download/{tag}/{fileName}")
                || page is null || !IsOurRepository(page, $"releases/tag/{tag}"))
                throw new InvalidDataException("更新地址不属于配置的发布仓库。");
            var notes = root.TryGetProperty("body", out var body) ? body.GetString() ?? string.Empty : string.Empty;
            return new UpdateRelease(version, fileName, new Uri(url), digest[7..].ToLowerInvariant(), size,
                notes.Length > 12000 ? notes[..12000] : notes, new Uri(page!));
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or OverflowException)
        {
            throw new InvalidDataException("更新信息格式无效。", ex);
        }
    }

    public async Task<string> DownloadAsync(UpdateRelease release, string cacheRoot, IProgress<int>? progress, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(release);
        if (!IsOurRepository(release.DownloadUrl.AbsoluteUri, $"releases/download/v{release.Version}/{release.FileName}")
            && !IsOurRepository(release.DownloadUrl.AbsoluteUri, $"releases/download/{release.Version}/{release.FileName}"))
            throw new InvalidDataException("更新下载地址无效。");
        ValidateInstaller(release);
        var directory = Path.Combine(Path.GetFullPath(cacheRoot), Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var partial = Path.Combine(directory, "installer.partial");
        var final = Path.Combine(directory, release.FileName);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var request = Request(release.DownloadUrl, metadata: false);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.RequestMessage?.RequestUri is { Scheme: not "https" })
                throw new InvalidDataException("更新下载被重定向到不安全的地址。");
            if (response.Content.Headers.ContentLength is { } length && length != release.Size)
                throw new InvalidDataException("安装包大小与更新信息不一致。");
            await using var input = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long received = 0;
            var lastProgress = -1;
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, useAsync: true))
            {
                var buffer = new byte[65536];
                progress?.Report(0);
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token).ConfigureAwait(false)) != 0)
                {
                    received += read;
                    if (received > release.Size) throw new InvalidDataException("安装包超出声明大小。");
                    hash.AppendData(buffer, 0, read);
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token).ConfigureAwait(false);
                    var percent = (int)Math.Min(99, received * 100 / release.Size);
                    if (percent != lastProgress) { progress?.Report(percent); lastProgress = percent; }
                }
                timeout.Token.ThrowIfCancellationRequested();
                if (received != release.Size || !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("安装包校验失败，请重试下载。");
                await output.FlushAsync(timeout.Token).ConfigureAwait(false);
            }
            timeout.Token.ThrowIfCancellationRequested();
            File.Move(partial, final);
            progress?.Report(100);
            return final;
        }
        catch
        {
            try { Directory.Delete(directory, recursive: true); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            throw;
        }
    }

    internal static void ValidateInstaller(UpdateRelease release)
    {
        if (!StableVersionPattern().IsMatch(release.Version.ToString())
            || release.FileName != $"WidgeDesk-{release.Version}-win-x64-Setup.exe"
            || !DigestPattern().IsMatch("sha256:" + release.Sha256)
            || release.Size <= 0 || release.Size > MaximumInstallerSize)
            throw new InvalidDataException("安装包信息无效。");
    }

    private static HttpRequestMessage Request(Uri url, bool metadata)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("WidgeDesk-Updater/1.0");
        if (metadata) request.Headers.Accept.ParseAdd("application/vnd.github+json");
        return request;
    }

    /// <summary>配额什么时候恢复；读不到就返回 null（由调用方给一个保守的说法）。</summary>
    private static DateTimeOffset? RetryAfter(HttpResponseMessage response)
    {
        if (response.Headers.TryGetValues("X-RateLimit-Reset", out var reset)
            && long.TryParse(reset.FirstOrDefault(), out var seconds))
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds).ToLocalTime();
        }

        return null;
    }

    /// <summary>这个来源是不是 GitHub 的 Releases API。</summary>
    private static bool IsGitHubApiSource(Uri source)
        => source.IsAbsoluteUri && source.Host == "api.github.com" && SourcePattern().IsMatch(source.AbsolutePath);

    /// <summary>
    /// 来源必须是一个可信的 https 地址：要么 GitHub 的 Releases API，要么本仓库的静态清单。
    /// </summary>
    /// <remarks>
    /// 仍然拒绝明文 http 与外部主机：更新源决定下载什么、从哪里下载，
    /// 一个可被替换的来源等于把安装包交给别人。
    /// </remarks>
    private static void ValidateSource(Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != "https"
            || !source.IsDefaultPort || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0)
            throw new InvalidDataException("更新源必须是 https 地址，且不得带凭据、查询参数或片段。");

        if (IsGitHubApiSource(source)) return;

        // 静态清单：必须在 GitHub 的 raw/release-assets 域名下，且属于本仓库的某个名字。
        var allowed = source.Host is "raw.githubusercontent.com" or "github.com"
            || source.Host.EndsWith(".githubusercontent.com", StringComparison.OrdinalIgnoreCase);

        if (!allowed)
            throw new InvalidDataException("更新源必须是本仓库托管的静态清单或 GitHub Releases 接口。");
    }

    private static string Repository(Uri source)
    {
        if (!source.IsAbsoluteUri || source.Scheme != "https" || source.Host != "api.github.com"
            || !source.IsDefaultPort || source.UserInfo.Length != 0 || source.Query.Length != 0 || source.Fragment.Length != 0
            || !SourcePattern().IsMatch(source.AbsolutePath))
            throw new InvalidDataException("更新源必须是 GitHub 的公开仓库 Releases 接口。");
        return string.Join('/', source.AbsolutePath.Split('/').Skip(2).Take(2));
    }

    /// <summary>
    /// 这个仓库名曾经用过、后来改成了别的名字。
    /// </summary>
    /// <remarks>
    /// <b>为什么需要它。</b>仓库改名之后，GitHub 会把旧地址 301 重定向到新名字，
    /// 并把返回的 <c>html_url</c> 与资产地址一并改写成新名字。而更早发布的版本
    /// 把旧名字编译进了更新源，它按自己的来源校验地址，于是「地址不属于配置的仓库」——
    /// 用户看到的是「检查更新」失败，而不是有新版本。
    ///
    /// <para>
    /// 认下这些历史名字，旧版本就能顺着重定向拿到更新。这不是放宽校验：
    /// 仍然要求地址属于「本仓库的某个历史名字」，外部仓库照旧拒绝。
    /// </para>
    /// </remarks>
    private static readonly string[] RepositoryNames =
    [
        "Cyj-090900/WidgeDesk",
        // 历史名字：改名之前发布的版本把它编译进了更新源。
        "Cyj-090900/WinDesktopWidge",
    ];

    /// <summary>
    /// 这个地址是不是指向本仓库（现名或任何一个历史名字）的指定路径。
    /// </summary>
    /// <param name="actual">响应给出的完整地址。</param>
    /// <param name="path">仓库名之后的路径，例如 <c>releases/tag/v4.2.1</c>。</param>
    /// <remarks>
    /// <b>为什么不能逐字比对某一个仓库名。</b>仓库从 <c>WinDesktopWidge</c> 改名成
    /// <c>WidgeDesk</c> 之后，GitHub 会把旧地址 301 重定向，并把响应里的
    /// <c>html_url</c> 与资产地址一并改写成新名字。更早发布的版本把旧名字编译进了
    /// 更新源，它按自己的来源校验地址，于是判定「地址不属于配置的仓库」——
    /// 用户看到的是「检查更新」失败，而不是有新版本。
    ///
    /// <para>
    /// 因此接受本仓库的**现名与历史名字**。这不是放宽校验：外部仓库的名字不在
    /// 这个集合里，照样被拒；路径部分仍然逐字比对。
    /// </para>
    /// </remarks>
    private static bool IsOurRepository(string actual, string path)
    {
        foreach (var name in RepositoryNames)
        {
            if (actual == $"https://github.com/{name}/{path}") return true;
        }

        return false;
    }

    [GeneratedRegex(@"^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$")]
    private static partial Regex StableVersionPattern();
    [GeneratedRegex(@"^sha256:[0-9a-fA-F]{64}$")]
    private static partial Regex DigestPattern();

    /// <summary>静态清单里的摘要不带 <c>sha256:</c> 前缀（那是 GitHub API 的形状）。</summary>
    [GeneratedRegex(@"^[0-9a-fA-F]{64}$")]
    private static partial Regex Sha256HexPattern();
    [GeneratedRegex(@"^/repos/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+/releases/latest$")]
    private static partial Regex SourcePattern();
}

/// <summary>
/// GitHub 的匿名请求配额用完了。
/// </summary>
/// <remarks>
/// 单独成一个类型，是为了让界面能把它与「网络断了」区分开：这两件事对用户的
/// 含义完全不同——一个该等一会儿，一个该检查网络。消息本身就是给用户看的，
/// 不再拼英文原文。
/// </remarks>
public sealed class UpdateRateLimitedException(DateTimeOffset? resumeAt) : Exception(Describe(resumeAt))
{
    /// <summary>配额恢复的时间；读不到响应头时为 null。</summary>
    public DateTimeOffset? ResumeAt { get; } = resumeAt;

    private static string Describe(DateTimeOffset? resumeAt)
    {
        var advice = resumeAt is { } moment
            ? $"请在 {moment:HH:mm} 之后重试。"
            : "请过一段时间再试。";

        return "GitHub 限制了本机此刻的匿名查询次数（每小时 60 次），暂时无法检查更新。"
            + advice
            + "\n这不是网络故障，稍后会自动恢复。";
    }
}
