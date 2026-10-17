using System.Security.Cryptography;
using FreeDomLauncher.Core;

namespace FreeDomLauncher.Infrastructure.Net;

/// <summary>
/// Безопасная загрузка файлов: только https с доменов Modrinth, ограничение параллельности,
/// проверка размера и SHA-512/SHA-1, запись через временный .part файл.
/// </summary>
public sealed class FileDownloader
{
    private readonly HttpClient _http;
    private readonly ISettingsService? _settings;
    private SemaphoreSlim _gate;
    private int _gateSize;

    public FileDownloader(HttpClient http, ISettingsService? settings = null)
    {
        _http = http; _settings = settings;
        _gateSize = Math.Clamp(settings?.Current.MaxParallelDownloads ?? 3, 1, 10);
        _gate = new SemaphoreSlim(_gateSize, _gateSize);
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0) _http.DefaultRequestHeaders.UserAgent.ParseAdd(Modrinth.ModrinthClient.UserAgent);
    }

    public static bool IsAllowedUrl(Uri url)
        => url.Scheme == Uri.UriSchemeHttps && (url.Host.Equals("modrinth.com", StringComparison.OrdinalIgnoreCase)
                                                || url.Host.EndsWith(".modrinth.com", StringComparison.OrdinalIgnoreCase));

    private SemaphoreSlim CurrentGate()
    {
        var want = Math.Clamp(_settings?.Current.MaxParallelDownloads ?? 3, 1, 10);
        if (want != _gateSize) { _gateSize = want; _gate = new SemaphoreSlim(want, want); }
        return _gate;
    }

    public async Task DownloadAsync(Uri url, string destinationPath, long expectedSize, string? sha1, string? sha512,
        IProgress<long>? progress, CancellationToken ct)
    {
        if (!IsAllowedUrl(url)) throw new InvalidOperationException("Загрузка с этого адреса запрещена (разрешены только https-адреса Modrinth).");
        var gate = CurrentGate();
        await gate.WaitAsync(ct);
        var part = destinationPath + ".part";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            var timeout = TimeSpan.FromSeconds(Math.Clamp(_settings?.Current.DownloadTimeoutSeconds ?? 60, 10, 600));
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);

            using var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"Сервер вернул ошибку {(int)resp.StatusCode} при скачивании файла.");
            // после редиректа домен тоже должен оставаться разрешённым
            var finalUri = resp.RequestMessage?.RequestUri;
            if (finalUri != null && !IsAllowedUrl(finalUri)) throw new InvalidOperationException("Сервер перенаправил загрузку на недопустимый адрес.");

            using var h1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var h512 = IncrementalHash.CreateHash(HashAlgorithmName.SHA512);
            long written = 0;
            await using (var src = await resp.Content.ReadAsStreamAsync(cts.Token))
            await using (var dst = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buf = new byte[81920];
                while (true)
                {
                    cts.CancelAfter(timeout);                       // таймаут простоя соединения
                    int n = await src.ReadAsync(buf, cts.Token);
                    if (n == 0) break;
                    await dst.WriteAsync(buf.AsMemory(0, n), cts.Token);
                    h1.AppendData(buf, 0, n); h512.AppendData(buf, 0, n);
                    written += n;
                    progress?.Report(written);
                }
                await dst.FlushAsync(cts.Token);
            }

            if (expectedSize > 0 && written != expectedSize)
                throw new InvalidDataException($"Размер скачанного файла ({written} Б) не совпадает с ожидаемым ({expectedSize} Б).");
            if (!string.IsNullOrEmpty(sha512) && !Hex(h512.GetHashAndReset()).Equals(sha512, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма SHA-512 не совпала — файл повреждён или подменён.");
            if (string.IsNullOrEmpty(sha512) && !string.IsNullOrEmpty(sha1) && !Hex(h1.GetHashAndReset()).Equals(sha1, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Контрольная сумма SHA-1 не совпала — файл повреждён или подменён.");

            File.Move(part, destinationPath, true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Время ожидания загрузки истекло. Проверьте интернет-соединение.");
        }
        finally
        {
            gate.Release();
            try { if (File.Exists(part)) File.Delete(part); } catch { /* ignore */ }
        }
    }

    public static string Hex(byte[] bytes) => Convert.ToHexString(bytes).ToLowerInvariant();

    public static async Task<string> Sha1OfFileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, true);
        using var h = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buf = new byte[81920]; int n;
        while ((n = await fs.ReadAsync(buf, ct)) > 0) h.AppendData(buf, 0, n);
        return Hex(h.GetHashAndReset());
    }
}
