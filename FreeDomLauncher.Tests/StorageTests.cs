using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Infrastructure.Storage;
using Xunit;

namespace FreeDomLauncher.Tests;

public class StorageTests
{
    private static (AppPaths paths, SettingsService settings, ProfileService profiles) Make(TempDir t)
    {
        var paths = new AppPaths(t.Path); paths.EnsureCreated();
        var settings = new SettingsService(paths);
        return (paths, settings, new ProfileService(paths, settings));
    }

    [Fact]
    public async Task Profile_is_created_saved_and_loaded_after_restart()
    {
        using var t = new TempDir();
        var (paths, settings, profiles) = Make(t);
        var p = await profiles.CreateAsync(new ProfileCreateRequest("Моя сборка", "1.21.1", LoaderType.Fabric, "0.16.0", 4096, null));

        Assert.True(Directory.Exists(p.FolderPath));
        Assert.True(Directory.Exists(Path.Combine(p.FolderPath, "mods")));
        Assert.StartsWith(settings.InstancesRoot, p.FolderPath);

        var reloaded = new ProfileService(paths, settings);          // «перезапуск»
        var all = await reloaded.GetAllAsync();
        Assert.Single(all);
        Assert.Equal(p.Id, all[0].Id);
        Assert.Equal("1.21.1", all[0].MinecraftVersion);
        Assert.Equal(LoaderType.Fabric, all[0].Loader);
        Assert.Equal(p.Id, (await reloaded.GetActiveAsync())!.Id);
    }

    [Fact]
    public async Task Profiles_have_isolated_folders_and_permanent_ids()
    {
        using var t = new TempDir();
        var (_, _, profiles) = Make(t);
        var a = await profiles.CreateAsync(new ProfileCreateRequest("Same", "1.21.1", LoaderType.Vanilla, null, 2048, null));
        var b = await profiles.CreateAsync(new ProfileCreateRequest("Same", "1.21.1", LoaderType.Vanilla, null, 2048, null));
        Assert.NotEqual(a.FolderPath, b.FolderPath);
        Assert.NotEqual(a.Id, b.Id);
        await profiles.RenameAsync(a.Id, "Renamed");
        var again = (await profiles.GetAllAsync()).First(x => x.Id == a.Id);
        Assert.Equal("Renamed", again.Name);
        Assert.Equal(a.FolderPath, again.FolderPath);
    }

    [Fact]
    public async Task Duplicate_copies_files_with_new_id_and_delete_removes_folder()
    {
        using var t = new TempDir();
        var (_, _, profiles) = Make(t);
        var a = await profiles.CreateAsync(new ProfileCreateRequest("A", "1.21.1", LoaderType.Fabric, "0.16", 2048, null));
        File.WriteAllText(Path.Combine(a.FolderPath, "mods", "x.jar"), "data");
        var b = await profiles.DuplicateAsync(a.Id);
        Assert.NotEqual(a.Id, b.Id);
        Assert.True(File.Exists(Path.Combine(b.FolderPath, "mods", "x.jar")));

        await profiles.DeleteAsync(b.Id);
        Assert.False(Directory.Exists(b.FolderPath));
        Assert.True(Directory.Exists(a.FolderPath));
    }

    [Fact]
    public async Task Delete_does_not_touch_folders_outside_instances_root()
    {
        using var t = new TempDir();
        var (paths, settings, profiles) = Make(t);
        var outside = Path.Combine(t.Path, "outside"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "keep.txt"), "x");
        var p = await profiles.CreateAsync(new ProfileCreateRequest("A", "1.21.1", LoaderType.Vanilla, null, 2048, null));
        // подменяем путь на чужой — удаление записи не должно удалять чужую папку
        var tampered = p.Clone(); tampered.FolderPath = outside;
        var file = new JsonFileStore();
        var pf = (await file.ReadAsync<ProfilesFile>(paths.ProfilesFile))!;
        pf.Profiles[0].FolderPath = outside;
        await file.WriteAsync(paths.ProfilesFile, pf);

        var fresh = new ProfileService(paths, settings);
        await fresh.DeleteAsync(p.Id);
        Assert.True(File.Exists(Path.Combine(outside, "keep.txt")));
    }

    [Fact]
    public async Task Settings_are_saved_between_restarts_and_normalized()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path); paths.EnsureCreated();
        var s = new SettingsService(paths);
        await s.SaveAsync(new AppSettings { Theme = "Light", DefaultMemoryMb = 8192, MaxParallelDownloads = 99, AccentColor = "bad" });

        var s2 = new SettingsService(paths);
        await s2.LoadAsync();
        Assert.Equal("Light", s2.Current.Theme);
        Assert.Equal(8192, s2.Current.DefaultMemoryMb);
        Assert.Equal(10, s2.Current.MaxParallelDownloads);          // ограничено
        Assert.Equal("#8B5CF6", s2.Current.AccentColor);            // некорректный цвет заменён
    }

    [Fact]
    public async Task Corrupted_json_is_recovered_from_backup()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path); paths.EnsureCreated();
        var s = new SettingsService(paths);
        await s.SaveAsync(new AppSettings { Theme = "Light" });
        await s.SaveAsync(new AppSettings { Theme = "System" });     // создаёт .bak с предыдущей версией
        File.WriteAllText(paths.SettingsFile, "{ broken json");

        var s2 = new SettingsService(paths);
        await s2.LoadAsync();
        Assert.Equal("Light", s2.Current.Theme);                     // восстановлено из копии
        Assert.Contains(Directory.GetFiles(paths.Root), f => f.Contains(".corrupt-"));
    }

    [Fact]
    public async Task Missing_settings_file_gives_defaults()
    {
        using var t = new TempDir();
        var paths = new AppPaths(t.Path);
        var s = new SettingsService(paths);
        await s.LoadAsync();
        Assert.Equal("Dark", s.Current.Theme);
        Assert.Equal(paths.DefaultInstancesRoot, s.InstancesRoot);
    }

    [Fact]
    public async Task Create_rejects_empty_name()
    {
        using var t = new TempDir();
        var (_, _, profiles) = Make(t);
        await Assert.ThrowsAsync<ArgumentException>(() => profiles.CreateAsync(new ProfileCreateRequest("  ", "1.21.1", LoaderType.Vanilla, null, 2048, null)));
    }
}
