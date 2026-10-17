using System.Net;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Infrastructure.Modrinth;
using Xunit;

namespace FreeDomLauncher.Tests;

public class ModrinthTests
{
    private static ModrinthClient Client(Func<HttpRequestMessage, HttpResponseMessage> fn, out FakeHandler handler)
    {
        handler = new FakeHandler(fn);
        return new ModrinthClient(new HttpClient(handler));
    }

    [Fact]
    public async Task Search_parses_real_shaped_response()
    {
        var json = """
        {"hits":[{"project_id":"AANobbMI","slug":"sodium","author":"jellysquid3","title":"Sodium","description":"fast",
        "categories":["optimization"],"display_categories":["optimization"],"downloads":12345,"icon_url":"https://cdn.modrinth.com/i.png",
        "date_created":"2021-01-03T00:00:00Z","date_modified":"2025-01-03T00:00:00Z","project_type":"mod","versions":["1.21.1"]}],
        "offset":0,"limit":20,"total_hits":1}
        """;
        string? url = null;
        var c = Client(r => { url = r.RequestUri!.ToString(); return Http.Json(json); }, out _);
        var res = await c.SearchAsync(new SearchQuery { Text = "sodium", Kind = ProjectKind.Mod, MinecraftVersion = "1.21.1", Loader = "fabric" });

        Assert.Equal(1, res.TotalHits);
        Assert.Equal("Sodium", res.Hits[0].Title);
        Assert.Equal(12345, res.Hits[0].Downloads);
        Assert.Contains("query=sodium", url);
        Assert.Contains("index=relevance", url);
        Assert.Contains(Uri.EscapeDataString("versions:1.21.1"), url);
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound, "не найдены")]
    [InlineData(HttpStatusCode.BadRequest, "отклонил")]
    [InlineData(HttpStatusCode.Forbidden, "403")]
    public async Task Http_errors_become_friendly_exceptions(HttpStatusCode code, string text)
    {
        var c = Client(_ => Http.Status(code), out _);
        var ex = await Assert.ThrowsAsync<ModrinthException>(() => c.GetProjectAsync("x"));
        Assert.Equal((int)code, ex.StatusCode);
        Assert.Contains(text, ex.Message);
    }

    [Fact]
    public async Task Rate_limit_is_reported()
    {
        var c = Client(_ =>
        {
            var r = Http.Status((HttpStatusCode)429);
            r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(60));
            return r;
        }, out _);
        var ex = await Assert.ThrowsAsync<ModrinthException>(() => c.SearchAsync(new SearchQuery()));
        Assert.True(ex.IsRateLimited);
        Assert.Contains("ограничил", ex.Message);
    }

    [Fact]
    public async Task Server_error_is_retried_once_then_reported()
    {
        var c = Client(_ => Http.Status(HttpStatusCode.InternalServerError), out var h);
        var ex = await Assert.ThrowsAsync<ModrinthException>(() => c.GetProjectAsync("x"));
        Assert.Equal(2, h.Calls);
        Assert.Contains("недоступен", ex.Message);
    }

    [Fact]
    public async Task Network_failure_is_friendly()
    {
        var c = Client(_ => throw new HttpRequestException("boom"), out _);
        var ex = await Assert.ThrowsAsync<ModrinthException>(() => c.GetProjectAsync("x"));
        Assert.Contains("Нет соединения", ex.Message);
    }

    [Fact]
    public async Task Malformed_json_is_reported()
    {
        var c = Client(_ => Http.Json("<html>oops</html>"), out _);
        var ex = await Assert.ThrowsAsync<ModrinthException>(() => c.GetProjectAsync("x"));
        Assert.Contains("разобрать", ex.Message);
    }

    [Fact]
    public async Task Cancellation_is_propagated()
    {
        var c = Client(_ => Http.Json("{}"), out _);
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => c.GetProjectAsync("x", cts.Token));
    }

    [Fact]
    public async Task Versions_request_contains_loader_and_game_version_filters()
    {
        string? url = null;
        var c = Client(r => { url = Uri.UnescapeDataString(r.RequestUri!.ToString()); return Http.Json("[]"); }, out _);
        await c.GetVersionsAsync("AANobbMI", new[] { "fabric" }, new[] { "1.21.1" });
        Assert.Contains("project/AANobbMI/version", url);
        Assert.Contains("loaders=[\"fabric\"]", url);
        Assert.Contains("game_versions=[\"1.21.1\"]", url);
    }

    [Fact]
    public async Task Version_parses_files_hashes_and_dependencies()
    {
        var json = """
        {"id":"v1","project_id":"p1","name":"N","version_number":"1.0","version_type":"release","game_versions":["1.21.1"],"loaders":["fabric"],
        "date_published":"2025-01-01T00:00:00Z","downloads":5,
        "files":[{"hashes":{"sha1":"aa","sha512":"bb"},"url":"https://cdn.modrinth.com/data/p1/a.jar","filename":"a.jar","primary":true,"size":10}],
        "dependencies":[{"version_id":null,"project_id":"dep","dependency_type":"required"}]}
        """;
        var c = Client(_ => Http.Json(json), out _);
        var v = await c.GetVersionAsync("v1");
        Assert.Equal("aa", v.PrimaryFile!.Sha1);
        Assert.Equal("bb", v.PrimaryFile.Sha512);
        Assert.Equal("required", v.Dependencies[0].DependencyType);
        Assert.Equal("dep", v.Dependencies[0].ProjectId);
    }

    // ---- facets ----
    [Fact]
    public void Facets_for_mods_restrict_to_client_loaders_and_version()
    {
        var f = ModrinthFacets.Build(new SearchQuery { Kind = ProjectKind.Mod, MinecraftVersion = "1.21.1" });
        Assert.Contains(f, g => g.SequenceEqual(new[] { "project_type:mod" }));
        Assert.Contains(f, g => g.Contains("categories:fabric") && g.Contains("categories:forge") && !g.Contains("categories:bukkit"));
        Assert.Contains(f, g => g.SequenceEqual(new[] { "versions:1.21.1" }));
    }

    [Fact]
    public void Facets_for_specific_loader_and_category()
    {
        var f = ModrinthFacets.Build(new SearchQuery { Kind = ProjectKind.Mod, Loader = "fabric", Category = "optimization" });
        Assert.Contains(f, g => g.SequenceEqual(new[] { "categories:fabric" }));
        Assert.Contains(f, g => g.SequenceEqual(new[] { "categories:optimization" }));
    }

    [Theory]
    [InlineData(ProjectKind.ResourcePack, "project_type:resourcepack")]
    [InlineData(ProjectKind.Shader, "project_type:shader")]
    [InlineData(ProjectKind.Modpack, "project_type:modpack")]
    public void Facets_use_matching_project_types(ProjectKind kind, string facet)
    {
        var f = ModrinthFacets.Build(new SearchQuery { Kind = kind });
        Assert.Contains(f, g => g.Contains(facet));
    }

    [Fact]
    public void Facets_for_plugins_use_server_loaders_only()
    {
        var f = ModrinthFacets.Build(new SearchQuery { Kind = ProjectKind.Plugin });
        Assert.Contains(f, g => g.Contains("categories:paper") && !g.Contains("categories:fabric"));
    }

    [Theory]
    [InlineData(SearchSort.Relevance, "relevance")]
    [InlineData(SearchSort.Downloads, "downloads")]
    [InlineData(SearchSort.Newest, "newest")]
    public void Sort_index_names(SearchSort s, string expected) => Assert.Equal(expected, ModrinthFacets.SortIndex(s));

    [Fact]
    public void Query_string_limits_page_size()
    {
        var qs = ModrinthFacets.BuildQueryString(new SearchQuery { Limit = 5000, Offset = -3 });
        Assert.Contains("limit=100", qs);
        Assert.Contains("offset=0", qs);
    }

    // ---- фильтрация версий ----
    [Fact]
    public void Version_filter_by_minecraft_and_loader_prefers_release_then_newest()
    {
        var api = new FakeModrinth();
        api.AddVersion("p", "a", "1.21.1", "fabric", "beta", day: 10);
        api.AddVersion("p", "b", "1.21.1", "fabric", "release", day: 2);
        api.AddVersion("p", "c", "1.21.1", "fabric", "release", day: 5);
        api.AddVersion("p", "d", "1.20.4", "fabric", "release", day: 20);
        api.AddVersion("p", "e", "1.21.1", "forge", "release", day: 25);

        var filtered = VersionFilter.Filter(api.Versions["p"], "1.21.1", new[] { "fabric" });
        Assert.Equal(new[] { "c", "b", "a" }, filtered.Select(v => v.Id).ToArray());
        Assert.Equal("c", VersionFilter.PickBest(api.Versions["p"], "1.21.1", new[] { "fabric" })!.Id);
        Assert.Null(VersionFilter.PickBest(api.Versions["p"], "1.19", new[] { "fabric" }));
    }

    [Fact]
    public void Version_filter_without_loaders_checks_only_minecraft()
    {
        var api = new FakeModrinth();
        api.AddVersion("p", "a", "1.21.1", "minecraft");
        Assert.Single(VersionFilter.Filter(api.Versions["p"], "1.21.1", null));
    }

    [Fact]
    public void Quilt_accepts_fabric_mods()
    {
        var names = LoaderType.Quilt.ModLoaderNames();
        Assert.Contains("fabric", names);
        Assert.Contains("quilt", names);
        Assert.Empty(LoaderType.Vanilla.ModLoaderNames());
    }
}
