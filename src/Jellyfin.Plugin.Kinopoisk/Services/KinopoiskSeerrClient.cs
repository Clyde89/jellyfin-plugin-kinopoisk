#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Microsoft.Extensions.Caching.Memory;

namespace Jellyfin.Plugin.Kinopoisk.Services;

/// <summary>Optional Seerr connection. User identity is always resolved on the server.</summary>
public sealed class KinopoiskSeerrClient
{
    public const string HttpClientName = "KinopoiskSeerr";
    private readonly IHttpClientFactory _http;
    private readonly IMemoryCache _cache;
    private readonly Func<PluginConfiguration> _configuration;

    public KinopoiskSeerrClient(IHttpClientFactory http, IMemoryCache cache,
        Func<PluginConfiguration> configuration)
    {
        _http = http;
        _cache = cache;
        _configuration = configuration;
    }

    public string Provider
    {
        get
        {
            var config = _configuration();
            if (config.RecommendationRequestProvider == "disabled") return "disabled";
            if (config.RecommendationRequestProvider == "enhanced") return "enhanced";
            if (config.RecommendationRequestProvider == "seerr"
                || !string.IsNullOrWhiteSpace(config.SeerrUrl)
                || !string.IsNullOrWhiteSpace(config.SeerrApiKey)) return "seerr";
            return "enhanced";
        }
    }

    public async Task<JsonElement> Status(Guid jellyfinUserId, CancellationToken token)
    {
        var provider = Provider;
        if (provider != "seerr")
            return JsonSerializer.SerializeToElement(new { provider, active = false, userFound = false,
                reason = provider == "disabled" ? "disabled" : "legacy" });
        var config = Connection();
        var user = await FindUser(config, jellyfinUserId, false, token).ConfigureAwait(false);
        if (!user.HasValue)
            return JsonSerializer.SerializeToElement(new { provider, active = true, userFound = false, reason = "unlinked" });
        // Use the same identity and permissions as requests, never the API key owner's identity.
        var me = await Send(config, "auth/me", user, null, token).ConfigureAwait(false);
        var permissions = me.TryGetProperty("permissions", out var value) ? value.GetInt64() : 0;
        return JsonSerializer.SerializeToElement(new { provider, active = true, userFound = true,
            jellyseerrUserId = user.Value.ToString(), canRequestMovie = CanRequest(permissions, false),
            canRequestTv = CanRequest(permissions, true) });
    }

    private static bool CanRequest(long permissions, bool tv)
        => (permissions & (2 | 32 | (tv ? 524288 : 262144))) != 0;

    public async Task<JsonElement> Get(Guid userId, string path, CancellationToken token)
    {
        var config = Connection();
        var user = await FindUser(config, userId, false, token).ConfigureAwait(false)
            ?? throw new KinopoiskSeerrException(403, "Учётная запись Jellyfin не связана с Seerr.");
        return await Send(config, path, user, null, token).ConfigureAwait(false);
    }

    public async Task<JsonElement> Request(Guid userId, JsonElement body, CancellationToken token)
    {
        if (body.ValueKind != JsonValueKind.Object
            || body.EnumerateObject().Any(p => p.Name is not ("mediaType" or "mediaId" or "seasons"))
            || !body.TryGetProperty("mediaId", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out var mediaId) || mediaId < 1
            || !body.TryGetProperty("mediaType", out var type) || type.ValueKind != JsonValueKind.String
            || type.GetString() is not ("movie" or "tv"))
            throw new KinopoiskSeerrException(400, "Некорректный запрос фильма или сериала.");
        if (type.GetString() == "tv"
            && (!body.TryGetProperty("seasons", out var seasons) || seasons.ValueKind != JsonValueKind.Array
                || seasons.GetArrayLength() is < 1 or > 500
                || seasons.EnumerateArray().Any(s => s.ValueKind != JsonValueKind.Number || !s.TryGetInt32(out var number) || number < 0)))
            throw new KinopoiskSeerrException(400, "Выберите сезоны сериала.");
        var config = Connection();
        // Re-resolve before every write. Never accept requestedBy or X-API-User from the browser.
        var user = await FindUser(config, userId, true, token).ConfigureAwait(false)
            ?? throw new KinopoiskSeerrException(403, "Учётная запись Jellyfin не связана с Seerr.");
        return await Send(config, "request", user, body, token).ConfigureAwait(false);
    }

    private (Uri Url, string Key) Connection()
    {
        var config = _configuration();
        if (Provider != "seerr") throw new KinopoiskSeerrException(409, "Подключение Seerr не выбрано.");
        if (!Uri.TryCreate(config.SeerrUrl.TrimEnd('/') + "/", UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(uri.UserInfo)
            || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)
            || string.IsNullOrWhiteSpace(config.SeerrApiKey))
            throw new KinopoiskSeerrException(409, "Укажите адрес и API-ключ Seerr в настройках плагина.");
        return (uri, config.SeerrApiKey.Trim());
    }

    private async Task<int?> FindUser((Uri Url, string Key) config, Guid userId, bool refresh, CancellationToken token)
    {
        if (userId == Guid.Empty) throw new KinopoiskSeerrException(403, "Войдите в Jellyfin.");
        var cacheKey = (nameof(KinopoiskSeerrClient), config.Url, config.Key, userId);
        if (!refresh && _cache.TryGetValue(cacheKey, out int cached)) return cached;
        int? match = null;
        // Bounded pagination; only this internal lookup uses the service identity.
        for (var skip = 0; skip < 10000; skip += 100)
        {
            var page = await Send(config, "user?take=100&skip=" + skip, null, null, token).ConfigureAwait(false);
            if (!page.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
                throw new KinopoiskSeerrException(502, "Не удалось проверить учётную запись Seerr.");
            foreach (var user in results.EnumerateArray())
            {
                if (!user.TryGetProperty("jellyfinUserId", out var linked) || linked.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(linked.GetString(), out var linkedId) || linkedId != userId) continue;
                if (!user.TryGetProperty("id", out var id) || !id.TryGetInt32(out var number) || number < 1 || match.HasValue)
                    throw new KinopoiskSeerrException(409, "Связь учётной записи Seerr неоднозначна.");
                match = number;
            }
            if (results.GetArrayLength() < 100)
            {
                if (match.HasValue) _cache.Set(cacheKey, match.Value, TimeSpan.FromSeconds(30));
                return match;
            }
        }
        throw new KinopoiskSeerrException(502, "Не удалось завершить проверку учётной записи Seerr.");
    }

    private async Task<JsonElement> Send((Uri Url, string Key) config, string path, int? user,
        JsonElement? body, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(20));
        token = timeout.Token;
        using var request = new HttpRequestMessage(body.HasValue ? HttpMethod.Post : HttpMethod.Get,
            new Uri(config.Url, "api/v1/" + path));
        request.Headers.Add("X-Api-Key", config.Key);
        if (user.HasValue) request.Headers.Add("X-Api-User", user.Value.ToString());
        request.Headers.Accept.ParseAdd("application/json");
        if (body.HasValue) request.Content = new StringContent(body.Value.GetRawText(), Encoding.UTF8, "application/json");
        using var client = _http.CreateClient(HttpClientName);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var status = (int)response.StatusCode;
            throw new KinopoiskSeerrException(status is >= 400 and < 500 ? status : 502,
                status == 403 ? "Недостаточно прав в Seerr." : status == 409 ? "Запрос уже существует в Seerr."
                : status == 429 ? "Достигнут лимит запросов Seerr." : "Seerr сейчас недоступен.");
        }
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await stream.ReadAsync(bytes, token).ConfigureAwait(false)) > 0)
        {
            if (buffer.Length + count > 4 * 1024 * 1024) throw new KinopoiskSeerrException(502, "Ответ Seerr слишком велик.");
            buffer.Write(bytes, 0, count);
        }
        using var document = JsonDocument.Parse(buffer.ToArray());
        return document.RootElement.Clone();
    }
}

public sealed class KinopoiskSeerrException : Exception
{
    public KinopoiskSeerrException(int status, string message) : base(message) { Status = status; }
    public int Status { get; }
}
