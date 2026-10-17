namespace FreeDomLauncher.Infrastructure.Storage;

/// <summary>Все пути данных лаунчера (%APPDATA%\FreeDomLauncher).</summary>
public sealed class AppPaths
{
    public string Root { get; }
    public AppPaths(string root) { Root = Path.GetFullPath(root); }

    public static AppPaths CreateDefault()
        => new(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FreeDomLauncher"));

    public string SettingsFile => Path.Combine(Root, "settings.json");
    public string ProfilesFile => Path.Combine(Root, "profiles.json");
    public string AccountsFile => Path.Combine(Root, "accounts.json");
    public string SecretsDir => Path.Combine(Root, "secrets");
    public string CacheDir => Path.Combine(Root, "cache");
    public string LogsDir => Path.Combine(Root, "logs");
    public string GameLogsDir => Path.Combine(Root, "logs", "games");
    public string DefaultInstancesRoot => Path.Combine(Root, "instances");
    /// <summary>Общие для всех сборок библиотеки, ассеты, версии и Java (экономит место).</summary>
    public string SharedMinecraftDir => Path.Combine(Root, "minecraft");
    public string PopularCacheFile => Path.Combine(CacheDir, "popular.json");
    public string VersionsCacheFile => Path.Combine(CacheDir, "mc_versions.json");

    public void EnsureCreated()
    {
        foreach (var d in new[] { Root, SecretsDir, CacheDir, LogsDir, GameLogsDir, SharedMinecraftDir })
            Directory.CreateDirectory(d);
    }
}
