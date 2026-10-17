using System.Net.Http.Json;
using System.Text.Json;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Minecraft;

/// <summary>Списки версий: манифест Mojang и Fabric Meta (официальные источники). Результат кэшируется на диск для работы офлайн.</summary>
public sealed class VersionCatalogService : IVersionCatalogService
{
    private const string MojangManifest = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";
    private const string FabricMeta = "https://meta.fabricmc.net/v2/";

    private sealed class VersionsCache { public List<MinecraftVersionInfo> Versions { get; set; } = new(); public DateTimeOffset UpdatedAt { get; set; } }

    private readonly HttpClient _http;
    private readonly AppPaths _paths;
    private readonly IAppLogger? _log;
    private readonly JsonFileStore _store = new();

    public VersionCatalogService(HttpClient http, AppPaths paths, IAppLogger? log = null)
    {
        _http = http; _paths = paths; _log = log;
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd(Modrinth.ModrinthClient.UserAgent);
    }

    public async Task<IReadOnlyList<MinecraftVersionInfo>> GetMinecraftVersionsAsync(bool includeSnapshots, CancellationToken ct = default)
    {
        List<MinecraftVersionInfo> all;
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(MojangManifest, ct));
            all = new();
            foreach (var v in doc.RootElement.GetProperty("versions").EnumerateArray())
            {
                var id = v.GetProperty("id").GetString()!;
                var type = v.GetProperty("type").GetString()!;
                var time = v.TryGetProperty("releaseTime", out var rt) && rt.TryGetDateTimeOffset(out var dto) ? dto : DateTimeOffset.MinValue;
                all.Add(new MinecraftVersionInfo(id, type, time));
            }
            await _store.WriteAsync(_paths.VersionsCacheFile, new VersionsCache { Versions = all, UpdatedAt = DateTimeOffset.UtcNow }, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException && !ct.IsCancellationRequested)
        {
            _log?.Warn("Не удалось получить список версий Minecraft, использую кэш: " + ex.Message);
            var cache = await _store.ReadAsync<VersionsCache>(_paths.VersionsCacheFile, ct);
            if (cache == null || cache.Versions.Count == 0)
                throw new InvalidOperationException("Не удалось загрузить список версий Minecraft. Проверьте подключение к интернету.", ex);
            all = cache.Versions;
        }
        return all.Where(v => includeSnapshots || v.Type == "release").OrderByDescending(v => v.ReleaseTime).ToList();
    }

    public async Task<string?> GetLatestFabricLoaderAsync(string minecraftVersion, CancellationToken ct = default)
    {
        try
        {
            using var doc = JsonDocument.Parse(await _http.GetStringAsync(FabricMeta + "versions/loader/" + Uri.EscapeDataString(minecraftVersion), ct));
            string? firstAny = null;
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                var loader = item.GetProperty("loader");
                var ver = loader.GetProperty("version").GetString();
                firstAny ??= ver;
                if (loader.TryGetProperty("stable", out var st) && st.ValueKind == JsonValueKind.True) return ver;
            }
            return firstAny;
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Fabric не поддерживает Minecraft {minecraftVersion} или сервер Fabric недоступен.", ex);
        }
    }

    public async Task<string> EnsureFabricProfileAsync(string minecraftVersion, string loaderVersion, string versionsDirectory, CancellationToken ct = default)
    {
        var id = $"fabric-loader-{loaderVersion}-{minecraftVersion}";
        var dir = Path.Combine(versionsDirectory, id);
        var file = Path.Combine(dir, id + ".json");
        if (File.Exists(file)) return id;

        string json;
        try
        {
            json = await _http.GetStringAsync(
                $"{FabricMeta}versions/loader/{Uri.EscapeDataString(minecraftVersion)}/{Uri.EscapeDataString(loaderVersion)}/profile/json", ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Не удалось скачать профиль Fabric {loaderVersion} для Minecraft {minecraftVersion}. Проверьте интернет и совместимость версий.", ex);
        }

        using (var doc = JsonDocument.Parse(json))
        {
            var gotId = doc.RootElement.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (gotId != id) throw new InvalidOperationException("Сервер Fabric вернул неожиданный профиль версии.");
        }
        Directory.CreateDirectory(dir);
        var tmp = file + ".tmp";
        await File.WriteAllTextAsync(tmp, json, ct);
        File.Move(tmp, file, true);
        return id;
    }
}
