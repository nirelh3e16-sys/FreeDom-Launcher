using System.Text.Json;
using System.Text.Json.Serialization;

namespace FreeDomLauncher.Infrastructure.Storage;

/// <summary>
/// Надёжное хранение JSON: атомарная запись через временный файл, резервная копия .bak,
/// восстановление из копии при повреждении основного файла.
/// </summary>
public sealed class JsonFileStore
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Возвращает null, если файла нет или он и его копия повреждены.</summary>
    public async Task<T?> ReadAsync<T>(string path, CancellationToken ct = default) where T : class
    {
        await _gate.WaitAsync(ct);
        try
        {
            var value = await TryReadAsync<T>(path, ct);
            if (value != null) return value;

            if (File.Exists(path))
            {
                // основной файл повреждён — сохраняем его для разбора и пробуем копию
                try { File.Move(path, path + ".corrupt-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss"), true); } catch { /* ignore */ }
            }
            return await TryReadAsync<T>(path + ".bak", ct);
        }
        finally { _gate.Release(); }
    }

    private static async Task<T?> TryReadAsync<T>(string path, CancellationToken ct) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, true);
            return await JsonSerializer.DeserializeAsync<T>(fs, Options, ct);
        }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }

    public async Task WriteAsync<T>(string path, T value, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            try
            {
                await using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, true))
                {
                    await JsonSerializer.SerializeAsync(fs, value, Options, ct);
                    await fs.FlushAsync(ct);
                }
                if (File.Exists(path))
                {
                    try { File.Copy(path, path + ".bak", true); } catch (IOException) { /* копия — best effort */ }
                }
                File.Move(tmp, path, true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* ignore */ }
                throw new IOException($"Не удалось сохранить файл «{Path.GetFileName(path)}»: {ex.Message}", ex);
            }
        }
        finally { _gate.Release(); }
    }
}
