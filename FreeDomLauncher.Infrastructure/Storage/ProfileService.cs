using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Util;

namespace FreeDomLauncher.Infrastructure.Storage;

public sealed class ProfileService : IProfileService
{
    private readonly AppPaths _paths;
    private readonly ISettingsService _settings;
    private readonly JsonFileStore _store = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ProfilesFile? _cache;

    public ProfileService(AppPaths paths, ISettingsService settings) { _paths = paths; _settings = settings; }

    public event EventHandler? Changed;

    private async Task<ProfilesFile> LoadAsync(CancellationToken ct)
    {
        _cache ??= await _store.ReadAsync<ProfilesFile>(_paths.ProfilesFile, ct) ?? new ProfilesFile();
        return _cache;
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        if (_cache != null) await _store.WriteAsync(_paths.ProfilesFile, _cache, ct);
    }

    private static GameProfile Copy(GameProfile p) => p.Clone();

    public async Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try { return (await LoadAsync(ct)).Profiles.Select(Copy).ToList(); }
        finally { _gate.Release(); }
    }

    public async Task<GameProfile?> GetActiveAsync(CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            var p = f.Profiles.FirstOrDefault(x => x.Id == f.ActiveProfileId) ?? f.Profiles.FirstOrDefault();
            return p == null ? null : Copy(p);
        }
        finally { _gate.Release(); }
    }

    public async Task SetActiveAsync(Guid id, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            if (f.Profiles.All(p => p.Id != id)) throw new InvalidOperationException("Сборка не найдена.");
            f.ActiveProfileId = id;
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private string MakeFolder(string name, Guid id)
    {
        var root = _settings.InstancesRoot;
        Directory.CreateDirectory(root);
        var folder = SafePath.Combine(root, $"{SafePath.Slug(name)}-{id.ToString("N")[..6]}");
        return folder;
    }

    public async Task<GameProfile> CreateAsync(ProfileCreateRequest r, CancellationToken ct = default)
    {
        var name = (r.Name ?? "").Trim();
        if (name.Length == 0) throw new ArgumentException("Введите название сборки.");
        if (name.Length > 64) throw new ArgumentException("Название сборки не должно быть длиннее 64 символов.");
        if (string.IsNullOrWhiteSpace(r.MinecraftVersion)) throw new ArgumentException("Выберите версию Minecraft.");

        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            var p = new GameProfile
            {
                Name = name,
                MinecraftVersion = r.MinecraftVersion,
                Loader = r.Loader,
                LoaderVersion = r.LoaderVersion,
                MemoryMb = Math.Clamp(r.MemoryMb, 512, 65536),
                JavaPath = string.IsNullOrWhiteSpace(r.JavaPath) ? null : r.JavaPath,
            };
            p.FolderPath = MakeFolder(name, p.Id);
            Directory.CreateDirectory(p.FolderPath);
            foreach (var sub in new[] { "mods", "resourcepacks", "shaderpacks", "saves", "config" })
                Directory.CreateDirectory(Path.Combine(p.FolderPath, sub));
            f.Profiles.Add(p);
            f.ActiveProfileId ??= p.Id;
            await SaveAsync(ct);
            Changed?.Invoke(this, EventArgs.Empty);
            return Copy(p);
        }
        finally { _gate.Release(); }
    }

    public async Task RenameAsync(Guid id, string newName, CancellationToken ct = default)
    {
        newName = (newName ?? "").Trim();
        if (newName.Length == 0) throw new ArgumentException("Название не может быть пустым.");
        if (newName.Length > 64) throw new ArgumentException("Название сборки не должно быть длиннее 64 символов.");
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            var p = f.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Сборка не найдена.");
            p.Name = newName;       // папка остаётся прежней: путь постоянный
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task UpdateAsync(GameProfile profile, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            var p = f.Profiles.FirstOrDefault(x => x.Id == profile.Id) ?? throw new InvalidOperationException("Сборка не найдена.");
            // идентификатор, папка и дата создания неизменяемы
            p.Name = string.IsNullOrWhiteSpace(profile.Name) ? p.Name : profile.Name.Trim();
            p.MinecraftVersion = profile.MinecraftVersion;
            p.Loader = profile.Loader;
            p.LoaderVersion = profile.LoaderVersion;
            p.JavaPath = string.IsNullOrWhiteSpace(profile.JavaPath) ? null : profile.JavaPath;
            p.MemoryMb = Math.Clamp(profile.MemoryMb, 512, 65536);
            p.LastPlayedAt = profile.LastPlayedAt;
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<GameProfile> DuplicateAsync(Guid id, CancellationToken ct = default)
    {
        GameProfile source;
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            source = Copy(f.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Сборка не найдена."));
        }
        finally { _gate.Release(); }

        var clone = source.Clone();
        clone.Id = Guid.NewGuid();
        clone.Name = source.Name + " (копия)";
        clone.CreatedAt = DateTimeOffset.UtcNow;
        clone.LastPlayedAt = null;
        clone.FolderPath = MakeFolder(clone.Name, clone.Id);

        await Task.Run(() => CopyDirectory(source.FolderPath, clone.FolderPath, ct), ct);

        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            f.Profiles.Add(clone);
            await SaveAsync(ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
        return Copy(clone);
    }

    private static void CopyDirectory(string from, string to, CancellationToken ct)
    {
        Directory.CreateDirectory(to);
        if (!Directory.Exists(from)) return;
        foreach (var file in Directory.EnumerateFiles(from))
        {
            ct.ThrowIfCancellationRequested();
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)), false);
        }
        foreach (var dir in Directory.EnumerateDirectories(from))
            CopyDirectory(dir, Path.Combine(to, Path.GetFileName(dir)), ct);
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        string? folderToDelete = null;
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            var p = f.Profiles.FirstOrDefault(x => x.Id == id) ?? throw new InvalidOperationException("Сборка не найдена.");
            f.Profiles.Remove(p);
            if (f.ActiveProfileId == id) f.ActiveProfileId = f.Profiles.FirstOrDefault()?.Id;
            await SaveAsync(ct);

            // Папку удаляем только если она лежит внутри папки сборок — защита от удаления чужих данных.
            var root = _settings.InstancesRoot;
            if (!string.IsNullOrEmpty(p.FolderPath) && SafePath.IsInside(root, p.FolderPath)
                && !string.Equals(Path.GetFullPath(p.FolderPath).TrimEnd(Path.DirectorySeparatorChar),
                                  Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
                folderToDelete = p.FolderPath;
        }
        finally { _gate.Release(); }

        if (folderToDelete != null && Directory.Exists(folderToDelete))
        {
            try { await Task.Run(() => Directory.Delete(folderToDelete, true), ct); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { throw new IOException("Сборка удалена из списка, но папку удалить не удалось (возможно, игра запущена): " + ex.Message, ex); }
        }
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
