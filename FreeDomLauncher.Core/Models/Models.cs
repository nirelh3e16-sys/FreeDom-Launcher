using System.Text.Json.Serialization;

namespace FreeDomLauncher.Core.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum LoaderType { Vanilla, Fabric, Forge, NeoForge, Quilt }

public static class LoaderTypeExtensions
{
    public static string DisplayName(this LoaderType l) => l switch
    {
        LoaderType.Vanilla => "Vanilla",
        LoaderType.Fabric => "Fabric",
        LoaderType.Forge => "Forge",
        LoaderType.NeoForge => "NeoForge",
        LoaderType.Quilt => "Quilt",
        _ => l.ToString()
    };

    /// <summary>Имя загрузчика в Modrinth API (null для Vanilla).</summary>
    public static string? ApiName(this LoaderType l) => l switch
    {
        LoaderType.Fabric => "fabric",
        LoaderType.Forge => "forge",
        LoaderType.NeoForge => "neoforge",
        LoaderType.Quilt => "quilt",
        _ => null
    };

    /// <summary>В версии 1.0.0 установка поддержана только для Vanilla и Fabric.</summary>
    public static bool IsInstallSupported(this LoaderType l) => l is LoaderType.Vanilla or LoaderType.Fabric;

    /// <summary>Какие загрузчики Modrinth подходят моду для данной сборки (Quilt умеет грузить Fabric-моды).</summary>
    public static IReadOnlyList<string> ModLoaderNames(this LoaderType l) => l switch
    {
        LoaderType.Quilt => new[] { "quilt", "fabric" },
        LoaderType.Vanilla => Array.Empty<string>(),
        _ => new[] { l.ApiName()! }
    };

    public static LoaderType? FromApiName(string? name) => name?.ToLowerInvariant() switch
    {
        "fabric" => LoaderType.Fabric,
        "forge" => LoaderType.Forge,
        "neoforge" => LoaderType.NeoForge,
        "quilt" => LoaderType.Quilt,
        _ => null
    };
}

/// <summary>Игровая сборка. Метаданные хранятся в profiles.json, моды — в freedom-mods.json внутри папки сборки.</summary>
public sealed class GameProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string MinecraftVersion { get; set; } = "";
    public LoaderType Loader { get; set; } = LoaderType.Vanilla;
    public string? LoaderVersion { get; set; }
    public string FolderPath { get; set; } = "";
    public string? JavaPath { get; set; }
    public int MemoryMb { get; set; } = 4096;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset? LastPlayedAt { get; set; }

    public GameProfile Clone() => (GameProfile)MemberwiseClone();
}

public sealed record ProfileCreateRequest(string Name, string MinecraftVersion, LoaderType Loader,
    string? LoaderVersion, int MemoryMb, string? JavaPath);

public sealed class ProfilesFile
{
    public Guid? ActiveProfileId { get; set; }
    public List<GameProfile> Profiles { get; set; } = new();
}

public sealed class AppSettings
{
    public string Theme { get; set; } = "Dark";            // System | Dark | Light
    public string AccentColor { get; set; } = "#8B5CF6";
    public bool AnimationsEnabled { get; set; } = true;
    public string? InstancesRoot { get; set; }              // null = папка по умолчанию
    public string? JavaPath { get; set; }                   // null = Java, которую скачает лаунчер
    public int DefaultMemoryMb { get; set; } = 4096;
    public int MaxParallelDownloads { get; set; } = 3;
    public int DownloadTimeoutSeconds { get; set; } = 60;
    public string LogLevel { get; set; } = "Info";          // Debug | Info | Warn | Error
    public int LogRetentionDays { get; set; } = 14;
    public bool IncludeSnapshots { get; set; }
    public string? MicrosoftClientId { get; set; }

    public AppSettings Clone() => (AppSettings)MemberwiseClone();

    /// <summary>Приводит значения к допустимым диапазонам (защита от испорченного JSON).</summary>
    public AppSettings Normalize()
    {
        if (Theme is not ("System" or "Dark" or "Light")) Theme = "Dark";
        if (LogLevel is not ("Debug" or "Info" or "Warn" or "Error")) LogLevel = "Info";
        DefaultMemoryMb = Math.Clamp(DefaultMemoryMb, 512, 65536);
        MaxParallelDownloads = Math.Clamp(MaxParallelDownloads, 1, 10);
        DownloadTimeoutSeconds = Math.Clamp(DownloadTimeoutSeconds, 10, 600);
        LogRetentionDays = Math.Clamp(LogRetentionDays, 1, 365);
        if (string.IsNullOrWhiteSpace(AccentColor) || !Util.ColorUtil.IsValidHex(AccentColor)) AccentColor = "#8B5CF6";
        if (string.IsNullOrWhiteSpace(InstancesRoot)) InstancesRoot = null;
        if (string.IsNullOrWhiteSpace(JavaPath)) JavaPath = null;
        if (string.IsNullOrWhiteSpace(MicrosoftClientId)) MicrosoftClientId = null;
        return this;
    }
}

// ---------- Аккаунты ----------
public sealed class AccountInfo
{
    public string Uuid { get; set; } = "";
    public string Username { get; set; } = "";
    public string Provider { get; set; } = "microsoft";
    public DateTimeOffset AddedAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class AccountsFile
{
    public string? ActiveUuid { get; set; }
    public List<AccountInfo> Accounts { get; set; } = new();
}

/// <summary>Сессия для запуска игры. Никогда не пишется на диск и в логи.</summary>
public sealed record AuthSession(string Username, string Uuid, string AccessToken);

public sealed record AuthResult(string Username, string Uuid, string MinecraftToken, DateTimeOffset ExpiresAt, string RefreshToken);

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, int ExpiresInSeconds, string? Message);

public sealed class AuthException : Exception
{
    public AuthException(string message, Exception? inner = null) : base(message, inner) { }
}

// ---------- Установленный контент ----------
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ContentKind { Mod, ResourcePack, Shader }

public sealed class InstalledModRecord
{
    public string? ProjectId { get; set; }
    public string? Slug { get; set; }
    public string? VersionId { get; set; }
    public string Title { get; set; } = "";
    public string VersionNumber { get; set; } = "";
    public string FileName { get; set; } = "";
    public string? Sha1 { get; set; }
    public ContentKind Kind { get; set; } = ContentKind.Mod;
    public string Source { get; set; } = "manual";   // modrinth | manual
    public bool Enabled { get; set; } = true;
    public bool IsDependency { get; set; }
    public List<string> GameVersions { get; set; } = new();
    public List<string> Loaders { get; set; } = new();
    public DateTimeOffset InstalledAt { get; set; } = DateTimeOffset.UtcNow;
}

public sealed class ModIndexFile
{
    public int SchemaVersion { get; set; } = 1;
    public List<InstalledModRecord> Mods { get; set; } = new();
}

public sealed record MinecraftVersionInfo(string Id, string Type, DateTimeOffset ReleaseTime);

public sealed record LaunchProgress(string Stage, double? Fraction);

public sealed record LaunchResult(bool Success, int? ProcessId, string? LogPath, string? Error);

public sealed class ProfileExitedEventArgs : EventArgs
{
    public Guid ProfileId { get; init; }
    public int ExitCode { get; init; }
    public string? LogPath { get; init; }
}

public sealed record PopularProject(string Slug, string Title);

public sealed class PopularCacheFile
{
    public DateTimeOffset UpdatedAt { get; set; }
    public List<PopularProject> Items { get; set; } = new();
}
