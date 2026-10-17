using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Core.Modrinth;
using FreeDomLauncher.Core.Mods;
using FreeDomLauncher.Core.Search;

namespace FreeDomLauncher.Core;

public interface IAppLogger
{
    string LogDirectory { get; }
    void Debug(string message);
    void Info(string message);
    void Warn(string message);
    void Error(string message, Exception? ex = null);
}

public interface ISettingsService
{
    AppSettings Current { get; }
    string InstancesRoot { get; }
    event EventHandler? Changed;
    Task LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(AppSettings settings, CancellationToken cancellationToken = default);
}

public interface IProfileService
{
    event EventHandler? Changed;
    Task<IReadOnlyList<GameProfile>> GetAllAsync(CancellationToken cancellationToken = default);
    Task<GameProfile?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task SetActiveAsync(Guid id, CancellationToken cancellationToken = default);
    Task<GameProfile> CreateAsync(ProfileCreateRequest request, CancellationToken cancellationToken = default);
    Task RenameAsync(Guid id, string newName, CancellationToken cancellationToken = default);
    Task UpdateAsync(GameProfile profile, CancellationToken cancellationToken = default);
    Task<GameProfile> DuplicateAsync(Guid id, CancellationToken cancellationToken = default);
    Task DeleteAsync(Guid id, CancellationToken cancellationToken = default);
}

public interface IModrinthService
{
    Task<ModrinthSearchResult> SearchAsync(SearchQuery query, CancellationToken cancellationToken = default);
    Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthProject>> GetProjectsAsync(IReadOnlyCollection<string> ids, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthMember>> GetMembersAsync(string idOrSlug, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModrinthVersion>> GetVersionsAsync(string projectId, IReadOnlyCollection<string>? loaders, IReadOnlyCollection<string>? gameVersions, CancellationToken cancellationToken = default);
    Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(IReadOnlyCollection<string> sha1Hashes, CancellationToken cancellationToken = default);
    Task<IReadOnlyDictionary<string, ModrinthVersion>> GetLatestVersionsByHashesAsync(IReadOnlyCollection<string> sha1Hashes, IReadOnlyCollection<string>? loaders, IReadOnlyCollection<string>? gameVersions, CancellationToken cancellationToken = default);
}

public interface IVersionCatalogService
{
    Task<IReadOnlyList<MinecraftVersionInfo>> GetMinecraftVersionsAsync(bool includeSnapshots, CancellationToken cancellationToken = default);
    Task<string?> GetLatestFabricLoaderAsync(string minecraftVersion, CancellationToken cancellationToken = default);
    /// <summary>Скачивает профиль Fabric (JSON версии) в папку versions и возвращает идентификатор версии для запуска.</summary>
    Task<string> EnsureFabricProfileAsync(string minecraftVersion, string loaderVersion, string versionsDirectory, CancellationToken cancellationToken = default);
}

public interface IModManagementService
{
    Task<IReadOnlyList<InstalledModRecord>> GetInstalledAsync(GameProfile profile, bool identifyUnknown, CancellationToken cancellationToken = default);
    Task<InstallPlan> PlanInstallAsync(GameProfile profile, string projectIdOrSlug, string? versionId, CancellationToken cancellationToken = default);
    Task<InstallOutcome> ExecuteInstallAsync(GameProfile profile, InstallPlan plan, IProgress<string>? status, CancellationToken cancellationToken = default);
    Task SetEnabledAsync(GameProfile profile, InstalledModRecord record, bool enabled, CancellationToken cancellationToken = default);
    Task DeleteAsync(GameProfile profile, InstalledModRecord record, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<ModUpdate>> CheckUpdatesAsync(GameProfile profile, CancellationToken cancellationToken = default);
    Task UpdateAsync(GameProfile profile, ModUpdate update, CancellationToken cancellationToken = default);
}

public interface IMinecraftLauncherService
{
    event EventHandler<ProfileExitedEventArgs>? ProfileExited;
    bool IsRunning(Guid profileId);
    void Kill(Guid profileId);
    Task<LaunchResult> LaunchAsync(GameProfile profile, AuthSession session, IProgress<LaunchProgress> progress, CancellationToken cancellationToken = default);
}

public interface ISecretStore
{
    Task SaveAsync(string key, string secret, CancellationToken cancellationToken = default);
    Task<string?> LoadAsync(string key, CancellationToken cancellationToken = default);
    Task DeleteAsync(string key, CancellationToken cancellationToken = default);
}

public interface IAuthProvider
{
    string Id { get; }
    string DisplayName { get; }
    Task<AuthResult> SignInAsync(Action<DeviceCodeInfo> showDeviceCode, CancellationToken cancellationToken = default);
    Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken cancellationToken = default);
}

public interface IAuthenticationService
{
    event EventHandler? Changed;
    IReadOnlyList<IAuthProvider> Providers { get; }
    Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo?> GetActiveAsync(CancellationToken cancellationToken = default);
    Task<AccountInfo> AddAccountAsync(string providerId, Action<DeviceCodeInfo> showDeviceCode, CancellationToken cancellationToken = default);
    Task SetActiveAsync(string uuid, CancellationToken cancellationToken = default);
    Task RemoveAsync(string uuid, CancellationToken cancellationToken = default);
    Task<AuthSession> GetSessionAsync(string uuid, CancellationToken cancellationToken = default);
}

public interface IPopularProjectsStore
{
    Task<PopularCacheFile> LoadAsync(CancellationToken cancellationToken = default);
    Task SaveAsync(PopularCacheFile cache, CancellationToken cancellationToken = default);
}

public interface ISmartSearchService
{
    /// <summary>Возвращает подсказку (исправление раскладки/транслит/опечатка/контекст) или null, если уверенности недостаточно.</summary>
    Task<SmartSuggestion?> AnalyzeAsync(string query, ProjectKind kind, int originalTotalHits, CancellationToken cancellationToken = default);
    /// <summary>Обновляет локальный кэш популярных проектов, если он устарел.</summary>
    Task RefreshPopularAsync(CancellationToken cancellationToken = default);
}
