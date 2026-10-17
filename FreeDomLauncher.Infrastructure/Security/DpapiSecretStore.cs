using System.Security.Cryptography;
using System.Text;
using FreeDomLauncher.Core;
using FreeDomLauncher.Infrastructure.Storage;

namespace FreeDomLauncher.Infrastructure.Security;

/// <summary>Хранит секреты (токены) в файлах, зашифрованных Windows DPAPI для текущего пользователя.</summary>
public sealed class DpapiSecretStore : ISecretStore
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("FreeDomLauncher.v1");
    private readonly string _dir;

    public DpapiSecretStore(AppPaths paths) => _dir = paths.SecretsDir;

    private string FileFor(string key)
    {
        var safe = new string(key.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' ? c : '_').ToArray());
        if (safe.Length == 0) throw new ArgumentException("Пустой ключ секрета.");
        return Path.Combine(_dir, safe + ".dat");
    }

    private static void EnsureSupported()
    {
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Безопасное хранение токенов поддерживается только в Windows (DPAPI).");
    }

    public Task SaveAsync(string key, string secret, CancellationToken ct = default)
    {
        EnsureSupported();
        Directory.CreateDirectory(_dir);
        var protectedBytes = ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser);
        var path = FileFor(key);
        var tmp = path + ".tmp";
        File.WriteAllBytes(tmp, protectedBytes);
        File.Move(tmp, path, true);
        return Task.CompletedTask;
    }

    public Task<string?> LoadAsync(string key, CancellationToken ct = default)
    {
        EnsureSupported();
        var path = FileFor(key);
        if (!File.Exists(path)) return Task.FromResult<string?>(null);
        try
        {
            var bytes = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            return Task.FromResult<string?>(Encoding.UTF8.GetString(bytes));
        }
        catch (CryptographicException) { return Task.FromResult<string?>(null); }   // другой пользователь/ПК или повреждён
    }

    public Task DeleteAsync(string key, CancellationToken ct = default)
    {
        var path = FileFor(key);
        if (File.Exists(path)) File.Delete(path);
        return Task.CompletedTask;
    }
}
