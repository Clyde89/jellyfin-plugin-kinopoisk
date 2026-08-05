#nullable enable

using System;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Выдаёт автономный клиентский bundle КиноПоиска непосредственно из DLL плагина.
    /// </summary>
    [ApiController]
    [AllowAnonymous]
    [Route("Kinopoisk")]
    public sealed class KinopoiskWebClientController : ControllerBase
    {
        /// <summary>
        /// Возвращает клиентский JavaScript КиноПоиска.
        /// </summary>
        /// <returns>Версионированный JavaScript bundle.</returns>
        [HttpGet("WebClient.js")]
        [Produces("application/javascript")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status304NotModified)]
        public IActionResult GetWebClient()
        {
            var digest = KinopoiskWebClientBundle.Sha256;
            var etag = string.Concat('"', digest, '"');
            Response.Headers.ETag = etag;
            Response.Headers["X-Content-Type-Options"] = "nosniff";

            if (Request.Headers.TryGetValue("If-None-Match", out StringValues requestEtags)
                && requestEtags.Any(value => string.Equals(value, etag, StringComparison.Ordinal)))
            {
                return StatusCode(StatusCodes.Status304NotModified);
            }

            var requestedVersion = Request.Query["v"].ToString();
            Response.Headers.CacheControl = string.Equals(
                requestedVersion,
                KinopoiskWebClientBundle.VersionToken,
                StringComparison.Ordinal)
                ? "public, max-age=31536000, immutable"
                : "no-cache";

            return File(
                KinopoiskWebClientBundle.Bytes,
                "application/javascript; charset=utf-8");
        }
    }
}
