#nullable enable

using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Предоставляет административную диагностику NativeTrailerBridge.
    /// </summary>
    [ApiController]
    [Authorize(Policy = Policies.RequiresElevation)]
    [Route("KinopoiskPlayback/native-bridge")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class KinopoiskNativeTrailerBridgeController : ControllerBase
    {
        private readonly KinopoiskNativeTrailerBridge _bridge;
        private readonly ILogger<KinopoiskNativeTrailerBridgeController> _logger;

        public KinopoiskNativeTrailerBridgeController(
            KinopoiskNativeTrailerBridge bridge,
            ILogger<KinopoiskNativeTrailerBridgeController> logger)
        {
            _bridge = bridge ?? throw new ArgumentNullException(nameof(bridge));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskNativeTrailerBridgeResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<KinopoiskNativeTrailerBridgeResult>> GetStatus(
            int kinopoiskId,
            [FromQuery] Guid? movieId,
            CancellationToken cancellationToken)
        {
            if (!KinopoiskNativeTrailerBridge.IsAllowed(kinopoiskId))
                return BadRequest(CreateNotAllowed(kinopoiskId));

            return Ok(await _bridge
                .GetStatus(kinopoiskId, movieId, cancellationToken)
                .ConfigureAwait(false));
        }

        [HttpPost("{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskNativeTrailerBridgeResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<KinopoiskNativeTrailerBridgeResult>> Prepare(
            int kinopoiskId,
            [FromQuery] Guid? movieId,
            CancellationToken cancellationToken)
        {
            try
            {
                var result = await _bridge
                    .Prepare(kinopoiskId, movieId, cancellationToken)
                    .ConfigureAwait(false);
                return MapPrepareResult(result);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "NativeTrailerBridge не подготовлен для Kinopoisk ID {KinopoiskId} и фильма {MovieId}",
                    kinopoiskId,
                    movieId);
                return Problem(
                    statusCode: StatusCodes.Status500InternalServerError,
                    title: "Не удалось зарегистрировать NativeTrailerBridge.");
            }
        }

        [HttpDelete("{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskNativeTrailerBridgeResult), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status404NotFound)]
        [ProducesResponseType(StatusCodes.Status409Conflict)]
        public async Task<ActionResult<KinopoiskNativeTrailerBridgeResult>> Remove(
            int kinopoiskId,
            [FromQuery] Guid? movieId,
            CancellationToken cancellationToken)
        {
            var result = await _bridge
                .Remove(kinopoiskId, movieId, cancellationToken)
                .ConfigureAwait(false);
            return result.State switch
            {
                "not-allowed" => BadRequest(result),
                "movie-not-found" => NotFound(result),
                "ambiguous-movie" => Conflict(result),
                _ => Ok(result)
            };
        }

        private ActionResult<KinopoiskNativeTrailerBridgeResult> MapPrepareResult(
            KinopoiskNativeTrailerBridgeResult result)
            => result.State switch
            {
                "not-allowed" => BadRequest(result),
                "movie-not-found" => NotFound(result),
                "ambiguous-movie" => Conflict(result),
                "kinopoisk-hls-unavailable" => StatusCode(
                    StatusCodes.Status502BadGateway,
                    result),
                _ => Ok(result)
            };

        private static KinopoiskNativeTrailerBridgeResult CreateNotAllowed(int kinopoiskId)
            => new()
            {
                KinopoiskId = kinopoiskId,
                State = "not-allowed"
            };
    }
}
