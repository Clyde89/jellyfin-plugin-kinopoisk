#nullable enable

using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kinopoisk.Api;

[ApiController]
[Authorize]
[Route("KinopoiskRequests")]
public sealed class KinopoiskRequestsController : ControllerBase
{
    private readonly KinopoiskSeerrClient _seerr;
    public KinopoiskRequestsController(KinopoiskSeerrClient seerr) { _seerr = seerr; }

    // Jellyfin's authenticated principal is the only source of identity.
    private Guid UserId => Guid.TryParse(User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value, out var id)
        ? id : Guid.Empty;

    [HttpGet("user-status")]
    public Task<IActionResult> Status(CancellationToken token) => Execute(() => _seerr.Status(UserId, token), token);

    [HttpPost("connection-test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> CheckConnection([FromBody] SeerrConnectionTest options, CancellationToken token)
        => Execute(() => _seerr.CheckConnection(UserId, options.Provider, options.Url, options.ApiKey, token), token);

    [HttpGet("search")]
    public Task<IActionResult> Search([FromQuery] string query, CancellationToken token)
        => string.IsNullOrWhiteSpace(query) || query.Length > 200
            ? Task.FromResult<IActionResult>(BadRequest())
            : Execute(() => _seerr.Get(UserId, "search?query=" + Uri.EscapeDataString(query) + "&page=1", token), token);

    [HttpGet("{mediaType:regex(^(movie|tv)$)}/{id:int}")]
    public Task<IActionResult> Details(string mediaType, int id, CancellationToken token)
        => id < 1 ? Task.FromResult<IActionResult>(BadRequest())
            : Execute(() => _seerr.Get(UserId, mediaType + "/" + id, token), token);

    [HttpGet("{mediaType:regex(^(movie|tv)$)}/{id:int}/{list:regex(^(similar|recommendations)$)}")]
    public Task<IActionResult> Related(string mediaType, int id, string list, CancellationToken token)
        => id < 1 ? Task.FromResult<IActionResult>(BadRequest())
            : Execute(() => _seerr.Get(UserId, mediaType + "/" + id + "/" + list, token), token);

    [HttpGet("overrideRule")]
    public IActionResult Rules() => Ok(Array.Empty<object>());

    [HttpPost("request")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> CreateRequest([FromBody] JsonElement body, CancellationToken token)
        => Execute(() => _seerr.Request(UserId, body, token), token);

    private async Task<IActionResult> Execute(Func<Task<JsonElement>> action, CancellationToken token)
    {
        Response.Headers.CacheControl = "no-store";
        if (UserId == Guid.Empty) return Forbid();
        try { return Ok(await action().ConfigureAwait(false)); }
        catch (KinopoiskSeerrException error) { return StatusCode(error.Status, new { message = error.Message }); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (OperationCanceledException) { return StatusCode(504, new { message = "Seerr не ответил вовремя. Проверьте адрес и доступность сервиса." }); }
        catch (HttpRequestException) { return StatusCode(502, new { message = "Не удалось подключиться к Seerr. Проверьте адрес и доступность сервиса из Jellyfin." }); }
        catch (JsonException) { return StatusCode(502, new { message = "Получен ответ не в формате Seerr API. Проверьте адрес и путь подключения." }); }
        catch (Exception) { return StatusCode(502, new { message = "Seerr сейчас недоступен." }); }
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SeerrConnectionTest
{
    public string Provider { get; set; } = "auto";
    public string? Url { get; set; }
    public string? ApiKey { get; set; }
}
