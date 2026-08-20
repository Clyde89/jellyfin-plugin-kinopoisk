#nullable enable

using System;
using System.Net.Mime;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Предоставляет статистику кэша и безопасное состояние подготовки трейлера.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("KinopoiskPlayback/cache")]
    [Produces(MediaTypeNames.Application.Json)]
    public sealed class KinopoiskNativeTrailerCacheController : ControllerBase
    {
        private readonly IKinopoiskNativeTrailerCache _cache;
        private readonly IKinopoiskNativeTrailerCacheWarmupService _warmup;

        public KinopoiskNativeTrailerCacheController(
            IKinopoiskNativeTrailerCache cache,
            IKinopoiskNativeTrailerCacheWarmupService warmup)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _warmup = warmup ?? throw new ArgumentNullException(nameof(warmup));
        }

        /// <summary>
        /// Возвращает административную статистику с момента запуска Jellyfin.
        /// </summary>
        [HttpGet("statistics")]
        [Authorize(Policy = Policies.RequiresElevation)]
        [ProducesResponseType(typeof(KinopoiskNativeTrailerCacheStatistics), StatusCodes.Status200OK)]
        public ActionResult<KinopoiskNativeTrailerCacheStatistics> GetStatistics()
        {
            Response.Headers.CacheControl = "private, no-store";
            Response.Headers.Vary = "Authorization";
            return Ok(new KinopoiskNativeTrailerCacheStatistics
            {
                Cache = _cache.GetSnapshot(),
                Warmup = _warmup.GetSnapshot()
            });
        }

        /// <summary>
        /// Возвращает состояние одного трейлера без раскрытия локального пути или внешнего URL.
        /// </summary>
        [HttpGet("status/{kinopoiskId:int}")]
        [ProducesResponseType(typeof(KinopoiskNativeTrailerPreparationStatus), StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        public ActionResult<KinopoiskNativeTrailerPreparationStatus> GetStatus(int kinopoiskId)
        {
            if (kinopoiskId < 1)
                return BadRequest();

            Response.Headers.CacheControl = "private, no-store";
            Response.Headers.Vary = "Authorization";
            if (!_cache.Enabled)
            {
                return Ok(CreateStatus(
                    kinopoiskId,
                    "disabled",
                    cacheReady: false,
                    preparing: false,
                    "Серверный кэш трейлеров выключен."));
            }

            if (_cache.TryGet(kinopoiskId, false) is not null)
            {
                return Ok(CreateStatus(
                    kinopoiskId,
                    "ready",
                    cacheReady: true,
                    preparing: false,
                    "Трейлер готов к локальному воспроизведению."));
            }

            var preparing = _warmup.IsPreparing(kinopoiskId);
            return Ok(CreateStatus(
                kinopoiskId,
                preparing ? "preparing" : "missing",
                cacheReady: false,
                preparing,
                preparing
                    ? "Трейлер подготавливается на сервере. Воспроизведение начнётся автоматически."
                    : "Локальная копия ещё не подготовлена."));
        }

        private static KinopoiskNativeTrailerPreparationStatus CreateStatus(
            int kinopoiskId,
            string state,
            bool cacheReady,
            bool preparing,
            string message)
            => new()
            {
                KinopoiskId = kinopoiskId,
                State = state,
                CacheReady = cacheReady,
                Preparing = preparing,
                Message = message
            };
    }
}
