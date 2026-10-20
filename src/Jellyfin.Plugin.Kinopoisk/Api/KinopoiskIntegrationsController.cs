#nullable enable

using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kinopoisk.Api;

[ApiController]
[Authorize]
[Route("KinopoiskRequests/integrations")]
public sealed class KinopoiskIntegrationsController : ControllerBase
{
    private readonly KinopoiskEnhancedAvailability _enhanced;

    public KinopoiskIntegrationsController(KinopoiskEnhancedAvailability enhanced) => _enhanced = enhanced;

    [HttpGet]
    public IActionResult Get()
    {
        Response.Headers.CacheControl = "no-store";
        // Возвращалось только состояние известной интеграции, без списка плагинов и конфигураций.
        return Ok(new { enhanced = _enhanced.Read() });
    }
}
