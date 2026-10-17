using System.Net;
using System.Security.Cryptography;
using FreeDomLauncher.Core.Util;
using FreeDomLauncher.Infrastructure.Minecraft;
using FreeDomLauncher.Infrastructure.Net;
using Xunit;

namespace FreeDomLauncher.Tests;

public class SafePathTests
{
    [Fact]
    public void Combine_stays_inside_root()
    {
        using var t = new TempDir();
        var p = SafePath.Combine(t.Path, "mods", "a.jar");
        Assert.StartsWith(Path.GetFullPath(t.Path), p);
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../evil")]
    [InlineData("mods/../../evil")]
    public void Combine_rejects_traversal(string part)
    {
        using var t = new TempDir();
        Assert.Throws<InvalidOperationException>(() => SafePath.Combine(t.Path, part));
    }

    [Fact]
    public void Combine_rejects_absolute_parts()
    {
        using var t = new TempDir();
        var abs = Path.GetFullPath(Path.Combine(t.Path, "..", "x"));
        Assert.Throws<InvalidOperationException>(() => SafePath.Combine(t.Path, abs));
    }

    [Theory]
    [InlineData("a.jar", "a.jar")]
    [InlineData("../../evil.jar", "evil.jar")]
    [InlineData("..\\evil.jar", "evil.jar")]
    [InlineData("dir/sub/x.jar", "x.jar")]
    public void SanitizeFileName_strips_directories(string input, string expected) => Assert.Equal(expected, SafePath.SanitizeFileName(input));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("..")]
    [InlineData("a/")]
    public void SanitizeFileName_rejects_bad_names(string input) => Assert.Throws<InvalidOperationException>(() => SafePath.SanitizeFileName(input));

    [Fact]
    public void SanitizeFileName_replaces_invalid_chars()
    {
        if (!OperatingSystem.IsWindows()) return;   // на Linux символ '*' допустим в имени файла
        Assert.DoesNotContain('*', SafePath.SanitizeFileName("a*b?.jar"));
    }

    [Fact]
    public void IsInside_works()
    {
        using var t = new TempDir();
        Assert.True(SafePath.IsInside(t.Path, Path.Combine(t.Path, "x", "y")));
        Assert.False(SafePath.IsInside(t.Path, Path.Combine(t.Path, "..", "other")));
    }

    [Fact]
    public void Slug_is_filesystem_friendly()
    {
        Assert.Equal("my-pack-1-21", SafePath.Slug("My Pack 1.21!"));
        Assert.Equal("profile", SafePath.Slug("Моя сборка"));
    }
}

public class MiscTests
{
    [Fact]
    public void Markdown_is_converted_to_safe_plain_text()
    {
        var md = "# Title\n<script>alert(1)</script>Hello **bold** [link](https://x.y) ![img](https://i/x.png)\n<b>tag</b>";
        var text = MarkdownText.ToPlain(md);
        Assert.DoesNotContain("<", text);
        Assert.DoesNotContain("alert", text);
        Assert.DoesNotContain("https://x.y", text);
        Assert.Contains("Title", text);
        Assert.Contains("Hello bold link", text);
    }

    [Fact]
    public void Redactor_hides_tokens()
    {
        var s = Redactor.Clean("access_token=abc123secret and Bearer eyJhbGciOi.eyJzdWIiOiIx.sig12345 --accessToken TOPSECRET");
        Assert.DoesNotContain("abc123secret", s);
        Assert.DoesNotContain("TOPSECRET", s);
        Assert.DoesNotContain("eyJhbGciOi", s);
    }

    [Fact]
    public void Number_formatting()
    {
        Assert.Contains("млн", DownloadFormat.Count(2_500_000));
        Assert.Contains("тыс", DownloadFormat.Count(12_000));
        Assert.Equal("999", DownloadFormat.Count(999));
        Assert.Contains("МБ", DownloadFormat.Size(5 * 1024 * 1024));
    }

    [Theory]
    [InlineData("#8B5CF6", true)]
    [InlineData("#FF8B5CF6", true)]
    [InlineData("8B5CF6", false)]
    [InlineData("#ZZZZZZ", false)]
    public void Hex_color_validation(string v, bool ok) => Assert.Equal(ok, ColorUtil.IsValidHex(v));

    [Theory]
    [InlineData("openjdk version \"21.0.1\" 2023-10-17", "21.0.1")]
    [InlineData("java version \"1.8.0_392\"", "1.8.0_392")]
    public void Java_version_parsing(string output, string version) => Assert.Equal(version, JavaLocator.ParseVersion(output));

    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    public void Java_major_version(string v, int major) => Assert.Equal(major, JavaLocator.MajorVersion(v));

    [Fact]
    public async Task Missing_java_path_returns_null_version()
    {
        Assert.Null(await JavaLocator.GetVersionAsync(Path.Combine(Path.GetTempPath(), "no-such-java-" + Guid.NewGuid() + ".exe")));
    }
}

public class DownloaderTests
{
    [Theory]
    [InlineData("https://cdn.modrinth.com/data/a/b.jar", true)]
    [InlineData("https://modrinth.com/x", true)]
    [InlineData("http://cdn.modrinth.com/data/a/b.jar", false)]
    [InlineData("https://evil.com/modrinth.com", false)]
    [InlineData("https://modrinth.com.evil.com/x", false)]
    [InlineData("file:///C:/x.jar", false)]
    public void Only_https_modrinth_hosts_are_allowed(string url, bool allowed)
        => Assert.Equal(allowed, FileDownloader.IsAllowedUrl(new Uri(url)));

    private static FileDownloader Make(Func<HttpRequestMessage, HttpResponseMessage> fn)
        => new(new HttpClient(new FakeHandler(fn)), new FakeSettings());

    [Fact]
    public async Task Download_verifies_size_and_sha1()
    {
        using var t = new TempDir();
        var bytes = new byte[] { 5, 6, 7, 8 };
        var sha1 = Convert.ToHexString(SHA1.HashData(bytes)).ToLowerInvariant();
        var d = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) });
        var dest = Path.Combine(t.Path, "f.jar");
        await d.DownloadAsync(new Uri("https://cdn.modrinth.com/f.jar"), dest, 4, sha1, null, null, CancellationToken.None);
        Assert.Equal(bytes, File.ReadAllBytes(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Download_rejects_wrong_size()
    {
        using var t = new TempDir();
        var d = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[] { 1, 2 }) });
        var dest = Path.Combine(t.Path, "f.jar");
        await Assert.ThrowsAsync<InvalidDataException>(() => d.DownloadAsync(new Uri("https://cdn.modrinth.com/f.jar"), dest, 10, null, null, null, CancellationToken.None));
        Assert.False(File.Exists(dest));
        Assert.False(File.Exists(dest + ".part"));
    }

    [Fact]
    public async Task Download_rejects_foreign_host_without_request()
    {
        using var t = new TempDir();
        var handler = new FakeHandler(_ => Http.Status(HttpStatusCode.OK));
        var d = new FileDownloader(new HttpClient(handler), new FakeSettings());
        await Assert.ThrowsAsync<InvalidOperationException>(() => d.DownloadAsync(new Uri("https://evil.example/f.jar"), Path.Combine(t.Path, "f.jar"), 0, null, null, null, CancellationToken.None));
        Assert.Equal(0, handler.Calls);
    }

    [Fact]
    public async Task Download_http_error_is_reported()
    {
        using var t = new TempDir();
        var d = Make(_ => Http.Status(HttpStatusCode.NotFound));
        await Assert.ThrowsAsync<HttpRequestException>(() => d.DownloadAsync(new Uri("https://cdn.modrinth.com/f.jar"), Path.Combine(t.Path, "f.jar"), 0, null, null, null, CancellationToken.None));
    }

    [Fact]
    public async Task Download_can_be_cancelled()
    {
        using var t = new TempDir();
        var d = Make(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(new byte[10]) });
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => d.DownloadAsync(new Uri("https://cdn.modrinth.com/f.jar"), Path.Combine(t.Path, "f.jar"), 0, null, null, null, cts.Token));
    }
}
