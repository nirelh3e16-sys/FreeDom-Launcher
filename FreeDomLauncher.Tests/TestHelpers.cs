using System.Net;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "fdl-tests-" + Guid.NewGuid().ToString("N"));
    public TempDir() => Directory.CreateDirectory(Path);
    public void Dispose() { try { Directory.Delete(Path, true); } catch { /* ignore */ } }
}

public sealed class FakeHandler : HttpMessageHandler
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _fn;
    public int Calls { get; private set; }
    public FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> fn) => _fn = fn;
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        var r = _fn(request);
        r.RequestMessage ??= request;
        return Task.FromResult(r);
    }
}

public static class Http
{
    public static HttpResponseMessage Json(string json, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };
    public static HttpResponseMessage Status(HttpStatusCode code) => new(code) { Content = new StringContent("") };
}

public sealed class FakeSettings : ISettingsService
{
    public AppSettings Current { get; set; } = new AppSettings().Normalize();
    public string InstancesRoot { get; set; } = "";
    public event EventHandler? Changed { add { } remove { } }
    public Task LoadAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    public Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default) { Current = settings; return Task.CompletedTask; }
}

public sealed class NullLogger : IAppLogger
{
    public string LogDirectory => "";
    public void Debug(string message) { }
    public void Info(string message) { }
    public void Warn(string message) { }
    public void Error(string message, Exception? ex = null) { }
}

/// <summary>Простой фейковый Modrinth для тестов логики без сети.</summary>
public sealed class FakeModrinth : IModrinthService
{
    public Dictionary<string, ModrinthProject> Projects { get; } = new();
    public Dictionary<string, List<ModrinthVersion>> Versions { get; } = new();
    public Dictionary<string, ModrinthVersion> VersionsById { get; } = new();
    public int SearchTotal { get; set; } = 1;
    public List<string> SearchedTexts { get; } = new();

    public Task<ModrinthSearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        SearchedTexts.Add(query.Text);
        return Task.FromResult(new ModrinthSearchResult { TotalHits = SearchTotal });
    }
    public Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
        => Projects.TryGetValue(idOrSlug, out var p) ? Task.FromResult(p) : throw new ModrinthException("не найден", 404);
    public Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModrinthProject>>(ids.Where(Projects.ContainsKey).Select(i => Projects[i]).ToList());
    public Task<IReadOnlyList<ModrinthMember>> GetMembersAsync(string idOrSlug, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModrinthMember>>(new List<ModrinthMember>());
    public Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, IReadOnlyCollection<string>? loaders, IReadOnlyCollection<string>? gameVersions, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ModrinthVersion>>(Versions.TryGetValue(projectId, out var v) ? v : new List<ModrinthVersion>());
    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct = default)
        => VersionsById.TryGetValue(versionId, out var v) ? Task.FromResult(v) : throw new ModrinthException("не найдена", 404);
    public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyCollection<string> h, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(new Dictionary<string, ModrinthVersion>());
    public Task<IReadOnlyDictionary<string, ModrinthVersion>> GetLatestVersionsByHashesAsync(IReadOnlyCollection<string> h, IReadOnlyCollection<string>? l, IReadOnlyCollection<string>? g, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyDictionary<string, ModrinthVersion>>(new Dictionary<string, ModrinthVersion>());

    public ModrinthProject AddProject(string id, string title, string type = "mod", params string[] loaders)
    {
        var p = new ModrinthProject { Id = id, Slug = id, Title = title, ProjectType = type, Loaders = loaders.ToList() };
        Projects[id] = p; Projects[p.Slug] = p; return p;
    }

    public ModrinthVersion AddVersion(string projectId, string versionId, string mc = "1.21.1", string loader = "fabric",
        string type = "release", string file = "mod.jar", int day = 1, params ModrinthDependency[] deps)
    {
        var v = new ModrinthVersion
        {
            Id = versionId, ProjectId = projectId, VersionNumber = "1." + day, Name = versionId, VersionType = type,
            GameVersions = new() { mc }, Loaders = new() { loader }, DatePublished = new DateTimeOffset(2025, 1, day, 0, 0, 0, TimeSpan.Zero),
            Files = new() { new ModrinthFile { Filename = file, Primary = true, Size = 3, Url = "https://cdn.modrinth.com/data/" + file,
                Hashes = new() { ["sha1"] = "x" } } },
            Dependencies = deps.ToList()
        };
        if (!Versions.TryGetValue(projectId, out var list)) Versions[projectId] = list = new();
        list.Add(v); VersionsById[versionId] = v; return v;
    }
}
