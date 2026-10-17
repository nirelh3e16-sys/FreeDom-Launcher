using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using FreeDomLauncher.Core;
using FreeDomLauncher.Core.Models;

namespace FreeDomLauncher.Infrastructure.Security;

/// <summary>
/// Официальный вход Microsoft → Xbox Live → XSTS → Minecraft Services через Device Code Flow.
/// Пароль в лаунчер не вводится: пользователь подтверждает вход на https://microsoft.com/link в браузере.
/// Требует Azure-приложение (публичный клиент) — см. README.
/// </summary>
public sealed class MicrosoftAuthProvider : IAuthProvider
{
    public string Id => "microsoft";
    public string DisplayName => "Microsoft (Minecraft Java Edition)";

    private const string DeviceCodeUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/devicecode";
    private const string TokenUrl = "https://login.microsoftonline.com/consumers/oauth2/v2.0/token";
    private const string Scope = "XboxLive.signin offline_access";

    private readonly HttpClient _http;
    private readonly Func<string?> _clientId;
    private readonly IAppLogger? _log;

    public MicrosoftAuthProvider(HttpClient http, Func<string?> clientIdProvider, IAppLogger? log = null)
    { _http = http; _clientId = clientIdProvider; _log = log; }

    private string RequireClientId()
    {
        var id = _clientId()?.Trim();
        if (string.IsNullOrEmpty(id))
            throw new AuthException("Не указан Client ID приложения Microsoft. Откройте «Настройки» и вставьте Application (client) ID из Azure (см. README, раздел «Авторизация»).");
        return id;
    }

    // ---------- Device code ----------
    public async Task<AuthResult> SignInAsync(Action<DeviceCodeInfo> showDeviceCode, CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        var dc = await PostFormAsync(DeviceCodeUrl, new() { ["client_id"] = clientId, ["scope"] = Scope }, ct);
        if (dc.TryGetProperty("error", out _)) throw MapOAuthError(dc);

        var deviceCode = dc.GetProperty("device_code").GetString()!;
        var userCode = dc.GetProperty("user_code").GetString()!;
        var uri = dc.GetProperty("verification_uri").GetString()!;
        var expires = dc.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 900;
        var interval = dc.TryGetProperty("interval", out var iv) ? Math.Max(iv.GetInt32(), 1) : 5;
        var message = dc.TryGetProperty("message", out var m) ? m.GetString() : null;
        showDeviceCode(new DeviceCodeInfo(userCode, uri, expires, message));

        var deadline = DateTimeOffset.UtcNow.AddSeconds(expires);
        while (DateTimeOffset.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            var tok = await PostFormAsync(TokenUrl, new()
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = deviceCode,
            }, ct);

            if (tok.TryGetProperty("error", out var err))
            {
                var code = err.GetString();
                if (code == "authorization_pending") continue;
                if (code == "slow_down") { interval += 5; continue; }
                throw MapOAuthError(tok);
            }
            return await ChainAsync(tok, ct);
        }
        throw new AuthException("Время ожидания входа истекло. Нажмите «Добавить аккаунт» и попробуйте снова.");
    }

    public async Task<AuthResult> RefreshAsync(string refreshToken, CancellationToken ct = default)
    {
        var clientId = RequireClientId();
        var tok = await PostFormAsync(TokenUrl, new()
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        }, ct);
        if (tok.TryGetProperty("error", out _))
            throw new AuthException("Сессия Microsoft истекла. Удалите аккаунт и войдите заново.");
        return await ChainAsync(tok, ct);
    }

    // ---------- Xbox / Minecraft ----------
    private async Task<AuthResult> ChainAsync(JsonElement msTokens, CancellationToken ct)
    {
        var msAccess = msTokens.GetProperty("access_token").GetString()!;
        var refresh = msTokens.TryGetProperty("refresh_token", out var r) ? r.GetString() ?? "" : "";

        // 1. Xbox Live
        var xbl = await PostJsonAsync("https://user.auth.xboxlive.com/user/authenticate", new
        {
            Properties = new { AuthMethod = "RPS", SiteName = "user.auth.xboxlive.com", RpsTicket = "d=" + msAccess },
            RelyingParty = "http://auth.xboxlive.com",
            TokenType = "JWT"
        }, null, ct);
        var xblToken = xbl.GetProperty("Token").GetString()!;
        var uhs = xbl.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;

        // 2. XSTS
        var xsts = await PostJsonAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new
        {
            Properties = new { SandboxId = "RETAIL", UserTokens = new[] { xblToken } },
            RelyingParty = "rp://api.minecraftservices.com/",
            TokenType = "JWT"
        }, null, ct);
        var xstsToken = xsts.GetProperty("Token").GetString()!;

        // 3. Minecraft
        var mc = await PostJsonAsync("https://api.minecraftservices.com/authentication/login_with_xbox",
            new { identityToken = $"XBL3.0 x={uhs};{xstsToken}" }, null, ct);
        var mcToken = mc.GetProperty("access_token").GetString()!;
        var expiresIn = mc.TryGetProperty("expires_in", out var ei) ? ei.GetInt32() : 86400;

        // 4. Профиль (ник и UUID); 404 означает, что на аккаунте нет Minecraft Java Edition
        using var req = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var resp = await SendAsync(req, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound)
            throw new AuthException("На этом аккаунте Microsoft нет Minecraft: Java Edition. Купите игру или войдите другим аккаунтом.");
        if (!resp.IsSuccessStatusCode)
            throw new AuthException($"Не удалось получить профиль Minecraft (код {(int)resp.StatusCode}).");
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var name = doc.RootElement.GetProperty("name").GetString()!;
        var id = doc.RootElement.GetProperty("id").GetString()!;
        var uuid = Guid.TryParseExact(id, "N", out var g) ? g.ToString() : id;

        _log?.Info($"Вход выполнен: {name}");
        return new AuthResult(name, uuid, mcToken, DateTimeOffset.UtcNow.AddSeconds(expiresIn), refresh);
    }

    // ---------- HTTP ----------
    private async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req, CancellationToken ct)
    {
        try { return await _http.SendAsync(req, ct); }
        catch (HttpRequestException ex) { throw new AuthException("Нет соединения с серверами Microsoft/Xbox. Проверьте интернет.", ex); }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested) { throw new AuthException("Серверы Microsoft не ответили вовремя.", ex); }
    }

    private async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        using var resp = await SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new AuthException($"Неожиданный ответ сервера Microsoft (код {(int)resp.StatusCode}).", ex); }
    }

    private async Task<JsonElement> PostJsonAsync(string url, object body, string? bearer, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
        };
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        if (bearer != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        using var resp = await SendAsync(req, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);

        if (!resp.IsSuccessStatusCode)
        {
            if (url.Contains("xsts.auth.xboxlive.com")) throw MapXstsError(text);
            if (url.Contains("login_with_xbox") && resp.StatusCode == HttpStatusCode.Forbidden)
                throw new AuthException("Minecraft отклонил вход: Azure-приложение не одобрено Mojang/Microsoft для Minecraft API. " +
                                        "Подайте заявку на одобрение приложения (см. README, раздел «Авторизация»).");
            if (url.Contains("user.auth.xboxlive.com") && resp.StatusCode == HttpStatusCode.Unauthorized)
                throw new AuthException("Xbox Live отклонил токен Microsoft. Проверьте, что в приложении Azure выдано разрешение XboxLive.signin.");
            throw new AuthException($"Ошибка авторизации (код {(int)resp.StatusCode}) на шаге {new Uri(url).Host}.");
        }
        try { return JsonDocument.Parse(text).RootElement.Clone(); }
        catch (JsonException ex) { throw new AuthException("Неожиданный ответ сервера авторизации.", ex); }
    }

    private static AuthException MapXstsError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("XErr", out var x))
            {
                return x.GetInt64() switch
                {
                    2148916233 => new AuthException("У этого аккаунта Microsoft нет профиля Xbox. Создайте его на xbox.com и попробуйте снова."),
                    2148916235 => new AuthException("Xbox Live недоступен в вашей стране или регионе."),
                    2148916236 or 2148916237 => new AuthException("Для этого аккаунта требуется подтверждение возраста на сайте Xbox."),
                    2148916238 => new AuthException("Аккаунт принадлежит ребёнку: добавьте его в семейную группу Microsoft и разрешите онлайн-игру."),
                    var code => new AuthException($"Xbox Live отказал в доступе (код {code}).")
                };
            }
        }
        catch (JsonException) { /* ignore */ }
        return new AuthException("Xbox Live отказал в доступе.");
    }

    private static AuthException MapOAuthError(JsonElement e)
    {
        var code = e.TryGetProperty("error", out var c) ? c.GetString() : "unknown";
        return code switch
        {
            "authorization_declined" => new AuthException("Вход отменён в браузере."),
            "expired_token" or "bad_verification_code" => new AuthException("Код входа истёк. Нажмите «Добавить аккаунт» и попробуйте снова."),
            "invalid_client" or "unauthorized_client" => new AuthException(
                "Приложение Azure не принимает вход по коду. В Azure → Authentication включите «Allow public client flows» и убедитесь, что тип аккаунтов — «Personal Microsoft accounts»."),
            "invalid_request" or "invalid_scope" => new AuthException("Некорректный Client ID или не настроены разрешения приложения (XboxLive.signin)."),
            _ => new AuthException("Ошибка входа Microsoft: " + code)
        };
    }
}
