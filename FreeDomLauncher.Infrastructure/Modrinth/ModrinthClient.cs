using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Modrinth;

namespace FreeDomLauncher.Infrastructure.Modrinth;

/// <summary>Клиент официального Modrinth API v2 (https://docs.modrinth.com).</summary>
public sealed class ModrinthClient : IModrinthService
{
    public const string BaseUrl = "https://api.modrinth.com/v2/";
    public const string UserAgent = "FreeDomLauncher/1.0.0 (github.com/nirelh3e16-sys/FreeDom-Launcher)";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly HttpClient _http;
    private readonly IAppLogger? _log;

    public ModrinthClient(HttpClient http, IAppLogger? log = null)
    {
        _http = http; _log = log;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    // ---------- низкоуровневые вызовы ----------
    private async Task<T> SendAsync<T>(HttpMethod method, string relative, object? body, CancellationToken ct)
    {
        const int attempts = 2;
        for (int attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var req = new HttpRequestMessage(method, BaseUrl + relative);
            if (body != null) req.Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json");
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (OperationCanceledException ex)
            {
                if (attempt < attempts) continue;
                throw new ModrinthException("Modrinth не ответил вовремя. Проверьте подключение к интернету и повторите попытку.", null, ex);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < attempts) { await Task.Delay(400, ct); continue; }
                throw new ModrinthException("Нет соединения с Modrinth. Проверьте подключение к интернету.", null, ex);
            }

            using (resp)
            {
                var code = (int)resp.StatusCode;
                if (resp.IsSuccessStatusCode)
                {
                    try
                    {
                        var data = await resp.Content.ReadFromJsonAsync<T>(Json, ct);
                        if (data == null) throw new ModrinthException("Modrinth вернул пустой ответ.", code);
                        return data;
                    }
                    catch (JsonException ex)
                    {
                        throw new ModrinthException("Не удалось разобрать ответ Modrinth (возможно, API изменился).", code, ex);
                    }
                }

                if (code == 429)
                {
                    var wait = resp.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    if (attempt < attempts && wait <= TimeSpan.FromSeconds(5)) { await Task.Delay(wait, ct); continue; }
                    throw new ModrinthException("Modrinth временно ограничил число запросов. Подождите минуту и повторите попытку.", 429);
                }
                if (code >= 500 && attempt < attempts) { await Task.Delay(500, ct); continue; }

                _log?.Warn($"Modrinth {method} {relative.Split('?')[0]} -> {code}");
                throw new ModrinthException(code switch
                {
                    404 => "Проект или версия не найдены на Modrinth.",
                    400 or 422 => "Modrinth отклонил запрос (некорректные параметры).",
                    >= 500 => "Сервер Modrinth сейчас недоступен. Повторите позже.",
                    _ => $"Modrinth вернул ошибку {code}."
                }, code);
            }
        }
    }

    private Task<T> GetAsync<T>(string relative, CancellationToken ct) => SendAsync<T>(HttpMethod.Get, relative, null, ct);

    private static string JsonArray(IEnumerable<string> items) => Uri.EscapeDataString(JsonSerializer.Serialize(items));

    // ---------- IModrinthService ----------
    public Task<ModrinthSearchResult> SearchAsync(SearchQuery query, CancellationToken ct = default)
        => GetAsync<ModrinthSearchResult>(ModrinthFacets.BuildQueryString(query), ct);

    public Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct = default)
        => GetAsync<ModrinthProject>("project/" + Uri.EscapeDataString(idOrSlug), ct);

    public async Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IReadOnlyCollection<string> ids, CancellationToken ct = default)
    {
        var result = new List<ModrinthProject>();
        foreach (var chunk in ids.Where(i => !string.IsNullOrWhiteSpace(i)).Distinct().Chunk(50))
            result.AddRange(await GetAsync<List<ModrinthProject>>("projects?ids=" + JsonArray(chunk), ct));
        return result;
    }

    public async Task<IReadOnlyList<ModrinthMember>> GetMembersAsync(string idOrSlug, CancellationToken ct = default)
        => await GetAsync<List<ModrinthMember>>("project/" + Uri.EscapeDataString(idOrSlug) + "/members", ct);

    public async Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, IReadOnlyCollection<string>? loaders,
        IReadOnlyCollection<string>? gameVersions, CancellationToken ct = default)
    {
        var sb = new StringBuilder("project/" + Uri.EscapeDataString(projectId) + "/version");
        var parts = new List<string>();
        if (loaders is { Count: > 0 }) parts.Add("loaders=" + JsonArray(loaders));
        if (gameVersions is { Count: > 0 }) parts.Add("game_versions=" + JsonArray(gameVersions));
        if (parts.Count > 0) sb.Append('?').Append(string.Join('&', parts));
        return await GetAsync<List<ModrinthVersion>>(sb.ToString(), ct);
    }

    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct = default)
        => GetAsync<ModrinthVersion>("version/" + Uri.EscapeDataString(versionId), ct);

    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyCollection<string> sha1Hashes, CancellationToken ct = default)
    {
        if (sha1Hashes.Count == 0) return new Dictionary<string, ModrinthVersion>();
        return await SendAsync<Dictionary<string, ModrinthVersion>>(HttpMethod.Post, "version_files",
            new { hashes = sha1Hashes, algorithm = "sha1" }, ct);
    }

    public async Task<IReadOnlyDictionary<string, ModrinthVersion>> GetLatestVersionsByHashesAsync(IReadOnlyCollection<string> sha1Hashes,
        IReadOnlyCollection<string>? loaders, IReadOnlyCollection<string>? gameVersions, CancellationToken ct = default)
    {
        if (sha1Hashes.Count == 0) return new Dictionary<string, ModrinthVersion>();
        return await SendAsync<Dictionary<string, ModrinthVersion>>(HttpMethod.Post, "version_files/update",
            new { hashes = sha1Hashes, algorithm = "sha1", loaders = loaders ?? Array.Empty<string>(), game_versions = gameVersions ?? Array.Empty<string>() }, ct);
    }
}
