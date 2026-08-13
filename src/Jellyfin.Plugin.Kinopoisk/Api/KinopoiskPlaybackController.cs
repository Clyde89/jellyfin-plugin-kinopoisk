#nullable enable

using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Предоставляет клиентам единый серверный контракт воспроизведения трейлеров.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("KinopoiskPlayback")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class KinopoiskPlaybackController : ControllerBase
    {
        private readonly KinopoiskTrailerPlaybackService _playbackService;
        private readonly ILogger<KinopoiskPlaybackController> _logger;

        public KinopoiskPlaybackController(
            KinopoiskTrailerPlaybackService playbackService,
            ILogger<KinopoiskPlaybackController> logger)
        {
            _playbackService = playbackService
                ?? throw new ArgumentNullException(nameof(playbackService));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("capabilities")]
        [ProducesResponseType(typeof(KinopoiskPlaybackCapabilities), StatusCodes.Status200OK)]
        public ActionResult<KinopoiskPlaybackCapabilities> GetCapabilities()
        {
            Response.Headers.CacheControl = "private, max-age=300";
            Response.Headers.Vary = "Authorization";
            return Ok(new KinopoiskPlaybackCapabilities());
        }

        [HttpGet("trailers/{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskTrailerPlaybackResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<KinopoiskTrailerPlaybackResponse>> GetTrailer(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                return BadRequest();

            try
            {
                Response.Headers.CacheControl = "private, no-store";
                Response.Headers.Vary = "Authorization";
                var result = await _playbackService
                    .Get(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                if (result is null)
                    return NotFound();
                return Ok(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Трейлер для Kinopoisk ID {KinopoiskId} не подготовлен сервером",
                    kinopoiskId);
                return Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Трейлер КиноПоиска временно недоступен.");
            }
        }
    }
}
