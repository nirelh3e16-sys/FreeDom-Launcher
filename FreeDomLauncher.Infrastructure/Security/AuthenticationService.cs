using System.Text.Json;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Security;

/// <summary>Управление аккаунтами. Публичные данные — в accounts.json, токены — в DPAPI-хранилище.</summary>
public sealed class AuthenticationService : IAuthenticationService
{
    private sealed class SecretBlob
    {
        public string RefreshToken { get; set; } = "";
        public string MinecraftToken { get; set; } = "";
        public DateTimeOffset ExpiresAt { get; set; }
    }

    private readonly AppPaths _paths;
    private readonly ISecretStore _secrets;
    private readonly IAppLogger? _log;
    private readonly JsonFileStore _store = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, IAuthProvider> _providers;

    public AuthenticationService(AppPaths paths, ISecretStore secrets, IEnumerable<IAuthProvider> providers, IAppLogger? log = null)
    {
        _paths = paths; _secrets = secrets; _log = log;
        _providers = providers.ToDictionary(p => p.Id);
    }

    public event EventHandler? Changed;
    public IReadOnlyList<IAuthProvider> Providers => _providers.Values.ToList();

    private async Task<AccountsFile> LoadAsync(CancellationToken ct)
        => await _store.ReadAsync<AccountsFile>(_paths.AccountsFile, ct) ?? new AccountsFile();

    public async Task<IReadOnlyList<AccountInfo>> GetAccountsAsync(CancellationToken ct = default)
        => (await LoadAsync(ct)).Accounts;

    public async Task<AccountInfo?> GetActiveAsync(CancellationToken ct = default)
    {
        var f = await LoadAsync(ct);
        return f.Accounts.FirstOrDefault(a => a.Uuid == f.ActiveUuid) ?? f.Accounts.FirstOrDefault();
    }

    public async Task<AccountInfo> AddAccountAsync(string providerId, Action<DeviceCodeInfo> showDeviceCode, CancellationToken ct = default)
    {
        if (!_providers.TryGetValue(providerId, out var provider))
            throw new AuthException("Этот способ входа пока не поддерживается.");
        var result = await provider.SignInAsync(showDeviceCode, ct);

        await _gate.WaitAsync(ct);
        try
        {
            await SaveSecretAsync(result, ct);
            var f = await LoadAsync(ct);
            var acc = f.Accounts.FirstOrDefault(a => a.Uuid == result.Uuid);
            if (acc == null)
            {
                acc = new AccountInfo { Uuid = result.Uuid, Username = result.Username, Provider = provider.Id };
                f.Accounts.Add(acc);
            }
            else acc.Username = result.Username;
            f.ActiveUuid = acc.Uuid;
            await _store.WriteAsync(_paths.AccountsFile, f, ct);
            Changed?.Invoke(this, EventArgs.Empty);
            return acc;
        }
        finally { _gate.Release(); }
    }

    private Task SaveSecretAsync(AuthResult r, CancellationToken ct)
        => _secrets.SaveAsync("acct-" + r.Uuid, JsonSerializer.Serialize(new SecretBlob
        { RefreshToken = r.RefreshToken, MinecraftToken = r.MinecraftToken, ExpiresAt = r.ExpiresAt }), ct);

    public async Task SetActiveAsync(string uuid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            if (f.Accounts.All(a => a.Uuid != uuid)) throw new InvalidOperationException("Аккаунт не найден.");
            f.ActiveUuid = uuid;
            await _store.WriteAsync(_paths.AccountsFile, f, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RemoveAsync(string uuid, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var f = await LoadAsync(ct);
            f.Accounts.RemoveAll(a => a.Uuid == uuid);
            if (f.ActiveUuid == uuid) f.ActiveUuid = f.Accounts.FirstOrDefault()?.Uuid;
            await _store.WriteAsync(_paths.AccountsFile, f, ct);
            await _secrets.DeleteAsync("acct-" + uuid, ct);
        }
        finally { _gate.Release(); }
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<AuthSession> GetSessionAsync(string uuid, CancellationToken ct = default)
    {
        var f = await LoadAsync(ct);
        var acc = f.Accounts.FirstOrDefault(a => a.Uuid == uuid) ?? throw new AuthException("Аккаунт не найден.");

        var json = await _secrets.LoadAsync("acct-" + uuid, ct)
                   ?? throw new AuthException("Токены аккаунта не найдены (возможно, данные созданы под другим пользователем Windows). Удалите аккаунт и войдите заново.");
        var blob = JsonSerializer.Deserialize<SecretBlob>(json) ?? throw new AuthException("Данные аккаунта повреждены. Войдите заново.");

        if (blob.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2) && !string.IsNullOrEmpty(blob.MinecraftToken))
            return new AuthSession(acc.Username, acc.Uuid, blob.MinecraftToken);

        if (!_providers.TryGetValue(acc.Provider, out var provider) || string.IsNullOrEmpty(blob.RefreshToken))
            throw new AuthException("Сессия истекла. Удалите аккаунт и войдите заново.");

        _log?.Info($"Обновление токена для {acc.Username}");
        var refreshed = await provider.RefreshAsync(blob.RefreshToken, ct);
        if (string.IsNullOrEmpty(refreshed.RefreshToken)) refreshed = refreshed with { RefreshToken = blob.RefreshToken };
        await SaveSecretAsync(refreshed, ct);
        return new AuthSession(refreshed.Username, refreshed.Uuid, refreshed.MinecraftToken);
    }
}
