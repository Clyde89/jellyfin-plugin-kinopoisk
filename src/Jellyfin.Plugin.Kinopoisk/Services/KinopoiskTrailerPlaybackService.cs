#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    public interface IKinopoiskTrailerPlaybackService
    {
        Task<KinopoiskTrailerPlaybackResponse?> Get(
            int kinopoiskId,
            CancellationToken cancellationToken);
    }

    /// <summary>
    /// Выбирает воспроизводимый трейлер по правилу КиноПоиск, затем YouTube.
    /// </summary>
    public sealed class KinopoiskTrailerPlaybackService : IKinopoiskTrailerPlaybackService
    {
        private static readonly TimeSpan ResolutionTimeout = TimeSpan.FromSeconds(6);

        private readonly IKinopoiskApiClient _apiClient;
        private readonly IKinopoiskTrailerStreamResolver _streamResolver;
        private readonly ILogger<KinopoiskTrailerPlaybackService> _logger;

        public KinopoiskTrailerPlaybackService(
            IKinopoiskApiClient apiClient,
            IKinopoiskTrailerStreamResolver streamResolver,
            ILogger<KinopoiskTrailerPlaybackService> logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _streamResolver = streamResolver
                ?? throw new ArgumentNullException(nameof(streamResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<KinopoiskTrailerPlaybackResponse?> Get(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                throw new ArgumentOutOfRangeException(nameof(kinopoiskId));

            var response = await _apiClient
                .GetTrailers(kinopoiskId, cancellationToken)
                .ConfigureAwait(false);
            var candidates = KinopoiskTrailerSelector.SelectCandidates(
                response,
                new KinopoiskTrailerSelectionOptions());
            var youtubeSources = candidates
                .Where(candidate => candidate.SourceKind == KinopoiskTrailerSourceKind.YouTube)
                .Select(ToYoutubeSource)
                .Where(source => source is not null)
                .Cast<KinopoiskTrailerPlaybackSource>()
                .ToArray();

            foreach (var candidate in candidates
                .Where(item => item.SourceKind == KinopoiskTrailerSourceKind.KinopoiskWidget)
                .Take(2))
            {
                try
                {
                    using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken);
                    timeoutSource.CancelAfter(ResolutionTimeout);
                    var stream = await _streamResolver
                        .Resolve(candidate.Url, timeoutSource.Token)
                        .ConfigureAwait(false);
                    if (stream is null)
                        continue;

                    return new KinopoiskTrailerPlaybackResponse
                    {
                        KinopoiskId = kinopoiskId,
                        Selected = ToKinopoiskSource(candidate, stream),
                        Fallbacks = youtubeSources.Take(1).ToArray()
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning(
                        "Превышено время разрешения трейлера виджета для Kinopoisk ID {KinopoiskId}",
                        kinopoiskId);
                }
                catch (Exception exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Трейлер виджета для Kinopoisk ID {KinopoiskId} не разрешён в HLS",
                        kinopoiskId);
                }
            }

            if (youtubeSources.Length == 0)
                return null;

            return new KinopoiskTrailerPlaybackResponse
            {
                KinopoiskId = kinopoiskId,
                Selected = youtubeSources[0],
                Fallbacks = youtubeSources.Skip(1).Take(4).ToArray()
            };
        }

        private static KinopoiskTrailerPlaybackSource ToKinopoiskSource(
            KinopoiskTrailerCandidate candidate,
            KinopoiskResolvedTrailerStream stream)
            => new()
            {
                Provider = "kinopoisk",
                PlaybackKind = "hls",
                Name = candidate.Name,
                Url = stream.MediaUrl.AbsoluteUri,
                ContentType = stream.ContentType,
                RequestHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["Referer"] = stream.WidgetUrl.AbsoluteUri,
                    ["Origin"] = "https://widgets.kinopoisk.ru"
                }
            };

        private static KinopoiskTrailerPlaybackSource? ToYoutubeSource(
            KinopoiskTrailerCandidate candidate)
        {
            if (!KinopoiskTrailerSelector.TryNormalizeYoutubeUrl(
                    candidate.Url,
                    out var videoId,
                    out var canonicalUrl))
            {
                return null;
            }

            return new KinopoiskTrailerPlaybackSource
            {
                Provider = "youtube",
                PlaybackKind = "youtube",
                Name = candidate.Name,
                Url = canonicalUrl,
                VideoId = videoId,
                ContentType = "text/html"
            };
        }
    }
}
