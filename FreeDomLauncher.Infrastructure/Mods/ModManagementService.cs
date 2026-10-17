using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Core.Util;
using FreeDomLauncher.Infrastructure.Modrinth;
using FreeDomLauncher.Infrastructure.Net;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Mods;

/// <summary>
/// Установка, отключение (перемещение в «*-disabled»), удаление и обновление контента сборки.
/// Состояние хранится в freedom-mods.json внутри папки сборки и сверяется с файлами на диске.
/// </summary>
public sealed class ModManagementService : IModManagementService
{
    public const string IndexFileName = "freedom-mods.json";

    private readonly IModrinthService _api;
    private readonly FileDownloader _downloader;
    private readonly IAppLogger _log;
    private readonly JsonFileStore _store = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly DependencyResolver _resolver;

    public ModManagementService(IModrinthService api, FileDownloader downloader, IAppLogger log)
    {
        _api = api; _downloader = downloader; _log = log;
        _resolver = new DependencyResolver(api);
    }

    // ---------- пути ----------
    public static string FolderOf(ContentKind k) => k switch
    {
        ContentKind.ResourcePack => "resourcepacks",
        ContentKind.Shader => "shaderpacks",
        _ => "mods"
    };
    public static string DisabledFolderOf(ContentKind k) => FolderOf(k) + "-disabled";
    private static string[] Extensions(ContentKind k) => k == ContentKind.Mod ? new[] { ".jar" } : new[] { ".zip", ".jar" };
    private static string IndexPath(GameProfile p) => Path.Combine(p.FolderPath, IndexFileName);

    private static string FilePath(GameProfile p, InstalledModRecord r, bool? enabled = null)
    {
        var folder = (enabled ?? r.Enabled) ? FolderOf(r.Kind) : DisabledFolderOf(r.Kind);
        return SafePath.Combine(p.FolderPath, folder, SafePath.SanitizeFileName(r.FileName));
    }

    private static ContentKind? KindOf(ProjectKind k) => k switch
    {
        ProjectKind.Mod => ContentKind.Mod,
        ProjectKind.ResourcePack => ContentKind.ResourcePack,
        ProjectKind.Shader => ContentKind.Shader,
        _ => null
    };

    // ---------- индекс ----------
    private async Task<ModIndexFile> ReadIndexAsync(GameProfile p, CancellationToken ct)
        => await _store.ReadAsync<ModIndexFile>(IndexPath(p), ct) ?? new ModIndexFile();

    private Task WriteIndexAsync(GameProfile p, ModIndexFile idx, CancellationToken ct) => _store.WriteAsync(IndexPath(p), idx, ct);

    /// <summary>Синхронизирует индекс с содержимым папок: убирает записи без файлов, добавляет найденные вручную файлы.</summary>
    private async Task<ModIndexFile> SyncAsync(GameProfile p, CancellationToken ct)
    {
        var idx = await ReadIndexAsync(p, ct);
        var changed = false;

        foreach (var kind in Enum.GetValues<ContentKind>())
        {
            var enabledDir = Path.Combine(p.FolderPath, FolderOf(kind));
            var disabledDir = Path.Combine(p.FolderPath, DisabledFolderOf(kind));
            var onDisk = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);   // имя файла → включён
            void Scan(string dir, bool enabled)
            {
                if (!Directory.Exists(dir)) return;
                foreach (var f in Directory.EnumerateFiles(dir))
                {
                    var ext = Path.GetExtension(f);
                    if (Extensions(kind).Contains(ext, StringComparer.OrdinalIgnoreCase)) onDisk[Path.GetFileName(f)] = enabled;
                }
            }
            Scan(disabledDir, false);
            Scan(enabledDir, true);       // если файл есть в обеих папках — приоритет у включённого

            foreach (var rec in idx.Mods.Where(m => m.Kind == kind).ToList())
            {
                if (!onDisk.TryGetValue(rec.FileName, out var en)) { idx.Mods.Remove(rec); changed = true; continue; }
                if (rec.Enabled != en) { rec.Enabled = en; changed = true; }
            }
            foreach (var (name, en) in onDisk)
            {
                if (idx.Mods.Any(m => m.Kind == kind && string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase))) continue;
                idx.Mods.Add(new InstalledModRecord
                {
                    Kind = kind, FileName = name, Enabled = en, Source = "manual",
                    Title = Path.GetFileNameWithoutExtension(name), VersionNumber = ""
                });
                changed = true;
            }
        }
        if (changed) await WriteIndexAsync(p, idx, ct);
        return idx;
    }

    public async Task<IReadOnlyList<InstalledModRecord>> GetInstalledAsync(GameProfile profile, bool identifyUnknown, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var idx = await SyncAsync(profile, ct);
            if (identifyUnknown)
            {
                var unknown = idx.Mods.Where(m => m.ProjectId == null && m.Source == "manual").ToList();
                if (unknown.Count > 0)
                {
                    try { await IdentifyAsync(profile, idx, unknown, ct); }
                    catch (ModrinthException ex) { _log.Warn("Не удалось опознать моды по хешам: " + ex.Message); }
                }
            }
            return idx.Mods.OrderBy(m => m.Kind).ThenBy(m => m.Title, StringComparer.OrdinalIgnoreCase).ToList();
        }
        finally { _gate.Release(); }
    }

    private async Task IdentifyAsync(GameProfile p, ModIndexFile idx, List<InstalledModRecord> unknown, CancellationToken ct)
    {
        var byHash = new Dictionary<string, InstalledModRecord>();
        foreach (var r in unknown)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                r.Sha1 ??= await FileDownloader.Sha1OfFileAsync(FilePath(p, r), ct);
                byHash[r.Sha1] = r;
            }
            catch (IOException) { /* файл занят — пропускаем */ }
        }
        if (byHash.Count == 0) return;
        var found = await _api.GetVersionsByHashesAsync(byHash.Keys.ToList(), ct);
        if (found.Count == 0) { await WriteIndexAsync(p, idx, ct); return; }

        var projects = (await _api.GetProjectsAsync(found.Values.Select(v => v.ProjectId).Distinct().ToList(), ct)).ToDictionary(x => x.Id);
        foreach (var (hash, ver) in found)
        {
            if (!byHash.TryGetValue(hash, out var rec)) continue;
            rec.ProjectId = ver.ProjectId; rec.VersionId = ver.Id; rec.VersionNumber = ver.VersionNumber;
            rec.GameVersions = ver.GameVersions; rec.Loaders = ver.Loaders; rec.Source = "modrinth";
            if (projects.TryGetValue(ver.ProjectId, out var proj)) { rec.Title = proj.Title; rec.Slug = proj.Slug; }
        }
        await WriteIndexAsync(p, idx, ct);
    }

    // ---------- планирование установки ----------
    public async Task<InstallPlan> PlanInstallAsync(GameProfile profile, string projectIdOrSlug, string? versionId, CancellationToken ct = default)
    {
        var plan = new InstallPlan();
        var project = await _api.GetProjectAsync(projectIdOrSlug, ct);
        var pk = ProjectKindExtensions.FromApiType(project.ProjectType) ?? ProjectKind.Mod;

        // «mod» с серверными загрузчиками — это плагин
        if (project.Loaders.Count > 0 && project.Loaders.All(l => ModrinthFacets.PluginLoaders.Contains(l, StringComparer.OrdinalIgnoreCase)))
            pk = ProjectKind.Plugin;

        if (!pk.IsInstallable())
        {
            plan.IsBlocked = true;
            plan.Warnings.Add(pk == ProjectKind.Plugin
                ? "Это серверный плагин: он не устанавливается в клиентскую папку mods. Откройте страницу проекта на Modrinth."
                : $"Тип проекта «{pk.Title()}» нельзя установить автоматически в версии 1.0.0. Откройте страницу проекта на Modrinth.");
            return plan;
        }

        string[]? loaders = null;
        if (pk == ProjectKind.Mod)
        {
            if (profile.Loader == LoaderType.Vanilla)
            {
                plan.IsBlocked = true;
                plan.Warnings.Add("Для установки модов нужна сборка с загрузчиком (например, Fabric). Эта сборка — Vanilla.");
                return plan;
            }
            loaders = profile.Loader.ModLoaderNames().ToArray();
            if (project.Loaders.Count > 0 && !project.Loaders.Intersect(loaders, StringComparer.OrdinalIgnoreCase).Any())
            {
                plan.IsBlocked = true;
                plan.Warnings.Add($"Мод не поддерживает {profile.Loader.DisplayName()}. Он работает с: {string.Join(", ", project.Loaders)}.");
                return plan;
            }
        }

        // версия
        ModrinthVersion? chosen;
        if (!string.IsNullOrEmpty(versionId))
        {
            var v = await _api.GetVersionAsync(versionId, ct);
            chosen = VersionFilter.IsCompatible(v, profile.MinecraftVersion, loaders) ? v : null;
            if (chosen == null)
            {
                plan.IsBlocked = true;
                plan.Warnings.Add($"Версия «{v.VersionNumber}» не подходит для Minecraft {profile.MinecraftVersion}" +
                                  (loaders != null ? $" и {profile.Loader.DisplayName()}." : "."));
                return plan;
            }
        }
        else
        {
            var all = await _api.GetVersionsAsync(project.Id, loaders, new[] { profile.MinecraftVersion }, ct);
            chosen = VersionFilter.PickBest(all, profile.MinecraftVersion, loaders);
            if (chosen == null)
            {
                plan.IsBlocked = true;
                var supported = project.GameVersions.AsEnumerable().Reverse().Take(6).ToList();
                plan.Warnings.Add($"Нет версии «{project.Title}» для Minecraft {profile.MinecraftVersion}" +
                                  (loaders != null ? $" и {profile.Loader.DisplayName()}." : ".") +
                                  (supported.Count > 0 ? $" Проект поддерживает: {string.Join(", ", supported)}…" : ""));
                return plan;
            }
        }

        var installed = await GetInstalledAsync(profile, false, ct);
        var dup = ModRules.FindDuplicate(installed, project.Id, chosen.PrimaryFile?.Filename);
        if (dup != null)
        {
            plan.IsBlocked = true;
            plan.Warnings.Add($"«{dup.Title}» уже установлен{(string.IsNullOrEmpty(dup.VersionNumber) ? "" : $" (версия {dup.VersionNumber})")}. Для смены версии используйте обновление в списке модов.");
            return plan;
        }

        var resolved = await _resolver.ResolveAsync(project, chosen, profile, installed, ct);
        // зависимости с совпадающим именем файла не ставим повторно
        foreach (var item in resolved.Items.Where(i => i.IsDependency).ToList())
            if (ModRules.FindDuplicate(installed, null, item.File.Filename) != null)
            {
                resolved.Items.Remove(item);
                resolved.Warnings.Add($"Файл «{item.File.Filename}» уже есть в сборке — пропущен.");
            }
        return resolved;
    }

    // ---------- установка ----------
    public async Task<InstallOutcome> ExecuteInstallAsync(GameProfile profile, InstallPlan plan, IProgress<string>? status, CancellationToken ct = default)
    {
        var outcome = new InstallOutcome();
        if (!plan.CanInstall) return outcome;

        var tasks = plan.Items.Select(async item =>
        {
            try
            {
                var kind = KindOf(item.Kind) ?? throw new InvalidOperationException("Этот тип проекта нельзя установить.");
                var name = SafePath.SanitizeFileName(item.File.Filename);
                if (!Extensions(kind).Contains(Path.GetExtension(name), StringComparer.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"Недопустимое расширение файла «{name}».");
                var dest = SafePath.Combine(profile.FolderPath, FolderOf(kind), name);

                if (File.Exists(dest))
                {
                    var existing = await FileDownloader.Sha1OfFileAsync(dest, ct);
                    if (!string.Equals(existing, item.File.Sha1, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"Файл «{name}» уже существует и отличается от скачиваемого.");
                }
                else
                {
                    status?.Report($"Скачивание: {item.Title}…");
                    if (!Uri.TryCreate(item.File.Url, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Некорректный адрес файла.");
                    await _downloader.DownloadAsync(uri, dest, item.File.Size, item.File.Sha1, item.File.Sha512, null, ct);
                }

                await _gate.WaitAsync(ct);
                try
                {
                    var idx = await ReadIndexAsync(profile, ct);
                    idx.Mods.RemoveAll(m => m.Kind == kind && string.Equals(m.FileName, name, StringComparison.OrdinalIgnoreCase));
                    idx.Mods.Add(new InstalledModRecord
                    {
                        ProjectId = item.ProjectId, Slug = item.Slug, VersionId = item.Version.Id, Title = item.Title,
                        VersionNumber = item.Version.VersionNumber, FileName = name, Sha1 = item.File.Sha1, Kind = kind,
                        Source = "modrinth", Enabled = true, IsDependency = item.IsDependency,
                        GameVersions = item.Version.GameVersions, Loaders = item.Version.Loaders
                    });
                    await WriteIndexAsync(profile, idx, ct);
                }
                finally { _gate.Release(); }
                lock (outcome) outcome.Installed.Add(item.Title);
                status?.Report($"Установлено: {item.Title}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _log.Error($"Не удалось установить {item.Title}", ex);
                lock (outcome) outcome.Failed.Add($"{item.Title}: {FriendlyError(ex)}");
            }
        }).ToList();

        await Task.WhenAll(tasks);
        return outcome;
    }

    private static string FriendlyError(Exception ex) => ex switch
    {
        InvalidDataException => ex.Message,
        TimeoutException => ex.Message,
        HttpRequestException => "ошибка сети при скачивании.",
        UnauthorizedAccessException => "нет доступа к папке сборки.",
        IOException io when io.HResult == unchecked((int)0x80070070) => "недостаточно места на диске.",
        IOException => ex.Message,
        InvalidOperationException => ex.Message,
        _ => ex.Message
    };

    // ---------- включить / выключить / удалить ----------
    public async Task SetEnabledAsync(GameProfile profile, InstalledModRecord record, bool enabled, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var idx = await SyncAsync(profile, ct);
            var rec = idx.Mods.FirstOrDefault(m => m.Kind == record.Kind && string.Equals(m.FileName, record.FileName, StringComparison.OrdinalIgnoreCase))
                      ?? throw new InvalidOperationException("Файл не найден в сборке.");
            if (rec.Enabled == enabled) return;

            var from = FilePath(profile, rec, rec.Enabled);
            var to = FilePath(profile, rec, enabled);
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            if (File.Exists(to)) throw new IOException($"В целевой папке уже есть файл «{rec.FileName}».");
            try { File.Move(from, to); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException("Не удалось переместить файл (возможно, игра запущена): " + ex.Message, ex); }
            rec.Enabled = enabled;
            await WriteIndexAsync(profile, idx, ct);
            record.Enabled = enabled;
        }
        finally { _gate.Release(); }
    }

    public async Task DeleteAsync(GameProfile profile, InstalledModRecord record, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var idx = await SyncAsync(profile, ct);
            var rec = idx.Mods.FirstOrDefault(m => m.Kind == record.Kind && string.Equals(m.FileName, record.FileName, StringComparison.OrdinalIgnoreCase));
            if (rec == null) return;
            var path = FilePath(profile, rec);
            try { if (File.Exists(path)) File.Delete(path); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException("Не удалось удалить файл (возможно, игра запущена): " + ex.Message, ex); }
            idx.Mods.Remove(rec);
            await WriteIndexAsync(profile, idx, ct);
        }
        finally { _gate.Release(); }
    }

    // ---------- обновления ----------
    public async Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(GameProfile profile, CancellationToken ct = default)
    {
        var installed = (await GetInstalledAsync(profile, true, ct)).Where(m => m.ProjectId != null && m.Source == "modrinth").ToList();
        var updates = new List<ModUpdate>();

        foreach (var group in installed.GroupBy(m => m.Kind))
        {
            var hashes = new Dictionary<string, InstalledModRecord>();
            foreach (var r in group)
            {
                ct.ThrowIfCancellationRequested();
                try { r.Sha1 ??= await FileDownloader.Sha1OfFileAsync(FilePath(profile, r), ct); hashes[r.Sha1] = r; }
                catch (IOException) { }
            }
            if (hashes.Count == 0) continue;
            IReadOnlyCollection<string>? loaders = group.Key == ContentKind.Mod ? profile.Loader.ModLoaderNames().ToArray() : null;
            var latest = await _api.GetLatestVersionsByHashesAsync(hashes.Keys.ToList(), loaders, new[] { profile.MinecraftVersion }, ct);
            foreach (var (hash, ver) in latest)
            {
                if (!hashes.TryGetValue(hash, out var rec)) continue;
                if (ver.Id != rec.VersionId && ver.PrimaryFile != null && VersionFilter.IsCompatible(ver, profile.MinecraftVersion, loaders))
                    updates.Add(new ModUpdate { Record = rec, NewVersion = ver });
            }
        }
        return updates;
    }

    public async Task UpdateAsync(GameProfile profile, ModUpdate update, CancellationToken ct = default)
    {
        var file = update.NewVersion.PrimaryFile ?? throw new InvalidOperationException("У новой версии нет файлов.");
        var rec = update.Record;
        var newName = SafePath.SanitizeFileName(file.Filename);
        if (!Extensions(rec.Kind).Contains(Path.GetExtension(newName), StringComparer.OrdinalIgnoreCase))
            throw new InvalidOperationException($"Недопустимое расширение файла «{newName}».");
        var folder = rec.Enabled ? FolderOf(rec.Kind) : DisabledFolderOf(rec.Kind);
        var dest = SafePath.Combine(profile.FolderPath, folder, newName);
        var oldPath = FilePath(profile, rec);
        var sameName = string.Equals(oldPath, dest, StringComparison.OrdinalIgnoreCase);

        // новый файл скачиваем рядом, старый убираем только после успешной проверки
        var tmpDest = sameName ? dest + ".new" : dest;
        if (!Uri.TryCreate(file.Url, UriKind.Absolute, out var uri)) throw new InvalidOperationException("Некорректный адрес файла.");
        await _downloader.DownloadAsync(uri, tmpDest, file.Size, file.Sha1, file.Sha512, null, ct);

        await _gate.WaitAsync(ct);
        try
        {
            try
            {
                if (File.Exists(oldPath)) File.Delete(oldPath);
                if (sameName) File.Move(tmpDest, dest, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException("Не удалось заменить файл (возможно, игра запущена): " + ex.Message, ex); }

            var idx = await ReadIndexAsync(profile, ct);
            idx.Mods.RemoveAll(m => m.Kind == rec.Kind && string.Equals(m.FileName, rec.FileName, StringComparison.OrdinalIgnoreCase));
            rec.FileName = newName; rec.VersionId = update.NewVersion.Id; rec.VersionNumber = update.NewVersion.VersionNumber;
            rec.Sha1 = file.Sha1; rec.GameVersions = update.NewVersion.GameVersions; rec.Loaders = update.NewVersion.Loaders;
            idx.Mods.Add(rec);
            await WriteIndexAsync(profile, idx, ct);
        }
        finally { _gate.Release(); }
    }
}
