#nullable enable

using System;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Presentation;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Предоставлены нормализованные данные карточки без раскрытия API-токена.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("KinopoiskPresentation")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class KinopoiskPresentationController : ControllerBase
    {
        private readonly KinopoiskPresentationService _presentationService;
        private readonly KinopoiskSupplementalApiClient _supplementalApiClient;
        private readonly ILogger<KinopoiskPresentationController> _logger;

        public KinopoiskPresentationController(
            KinopoiskPresentationService presentationService,
            KinopoiskSupplementalApiClient supplementalApiClient,
            ILogger<KinopoiskPresentationController> logger)
        {
            _presentationService = presentationService
                ?? throw new ArgumentNullException(nameof(presentationService));
            _supplementalApiClient = supplementalApiClient
                ?? throw new ArgumentNullException(nameof(supplementalApiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet("{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskPresentationResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<ActionResult<KinopoiskPresentationResponse>> GetPresentation(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                return BadRequest();

            try
            {
                SetPrivateCacheHeader(300);
                return Ok(await _presentationService
                    .Get(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Расширенная карточка Kinopoisk ID {KinopoiskId} не сформирована",
                    kinopoiskId);
                return Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Расширенная карточка КиноПоиска временно недоступна.");
            }
        }

        [HttpGet("{kinopoiskId:int}/facts")]
        [ProducesResponseType(typeof(KinopoiskFactsResponse), StatusCodes.Status200OK)]
        public Task<ActionResult<KinopoiskFactsResponse>> GetFacts(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => ExecuteSupplemental(
                kinopoiskId,
                "факты",
                token => _supplementalApiClient.GetFacts(kinopoiskId, token),
                cancellationToken);

        [HttpGet("{kinopoiskId:int}/box-office")]
        [ProducesResponseType(typeof(KinopoiskBoxOfficeResponse), StatusCodes.Status200OK)]
        public Task<ActionResult<KinopoiskBoxOfficeResponse>> GetBoxOffice(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => ExecuteSupplemental(
                kinopoiskId,
                "бюджет и сборы",
                token => _supplementalApiClient.GetBoxOffice(kinopoiskId, token),
                cancellationToken);

        [HttpGet("{kinopoiskId:int}/awards")]
        [ProducesResponseType(typeof(KinopoiskAwardsResponse), StatusCodes.Status200OK)]
        public Task<ActionResult<KinopoiskAwardsResponse>> GetAwards(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => ExecuteSupplemental(
                kinopoiskId,
                "награды",
                token => _supplementalApiClient.GetAwards(kinopoiskId, token),
                cancellationToken);

        [HttpGet("{kinopoiskId:int}/reviews")]
        [ProducesResponseType(typeof(KinopoiskReviewsResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public Task<ActionResult<KinopoiskReviewsResponse>> GetReviews(
            int kinopoiskId,
            [FromQuery] int page = 1,
            [FromQuery] string? order = null,
            CancellationToken cancellationToken = default)
        {
            if (page is < 1 or > 50)
                return Task.FromResult<ActionResult<KinopoiskReviewsResponse>>(BadRequest());

            return ExecuteSupplemental(
                kinopoiskId,
                "рецензии",
                token => _supplementalApiClient.GetReviews(
                    kinopoiskId,
                    page,
                    order,
                    token),
                cancellationToken);
        }

        private async Task<ActionResult<T>> ExecuteSupplemental<T>(
            int kinopoiskId,
            string section,
            Func<CancellationToken, Task<T>> action,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                return BadRequest();

            try
            {
                SetPrivateCacheHeader(1800);
                return Ok(await action(cancellationToken).ConfigureAwait(false));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Раздел {Section} для Kinopoisk ID {KinopoiskId} временно недоступен",
                    section,
                    kinopoiskId);
                return Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: $"Раздел «{section}» временно недоступен.");
            }
        }

        private void SetPrivateCacheHeader(int maximumAgeSeconds)
        {
            Response.Headers.CacheControl = $"private, max-age={maximumAgeSeconds}";
            Response.Headers.Vary = "Authorization";
        }
    }
}
