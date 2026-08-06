#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    /// <summary>
    /// Добавлено долговременное хранение нормализованных рецензий.
    /// </summary>
    public sealed class KinopoiskReviewCacheClient
    {
        private static readonly TimeSpan SuccessfulExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan EmptyExpiration = TimeSpan.FromMinutes(30);

        private readonly KinopoiskSupplementalApiClient _inner;
        private readonly PersistentJsonCache _persistentCache;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly ILogger<KinopoiskReviewCacheClient> _logger;

        public KinopoiskReviewCacheClient(
            KinopoiskSupplementalApiClient inner,
            PersistentJsonCache persistentCache,
            KinopoiskDiagnostics diagnostics,
            ILogger<KinopoiskReviewCacheClient> logger)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _persistentCache = persistentCache
                ?? throw new ArgumentNullException(nameof(persistentCache));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<KinopoiskReviewsResponse> GetReviews(
            int kinopoiskId,
            int page,
            string? order,
            CancellationToken cancellationToken)
        {
            var normalizedOrder = KinopoiskSupplementalApiClient.NormalizeReviewOrder(order);
            var cacheKey = CreateCacheKey(kinopoiskId, page, normalizedOrder);
            var cached = await _persistentCache
                .Read<KinopoiskReviewsResponse>(cacheKey, false, cancellationToken)
                .ConfigureAwait(false);
            if (cached.Found && cached.Value is not null)
            {
                _diagnostics.RecordPersistentCacheHit();
                return cached.Value;
            }

            try
            {
                var response = await _inner
                    .GetReviews(kinopoiskId, page, normalizedOrder, cancellationToken)
                    .ConfigureAwait(false);
                var expiration = response.Total > 0 && response.Items.Count > 0
                    ? SuccessfulExpiration
                    : EmptyExpiration;
                if (await _persistentCache
                    .Write(cacheKey, response, DateTimeOffset.UtcNow + expiration, cancellationToken)
                    .ConfigureAwait(false))
                {
                    _diagnostics.RecordPersistentCacheWrite();
                }

                return response;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                var stale = await _persistentCache
                    .Read<KinopoiskReviewsResponse>(cacheKey, true, cancellationToken)
                    .ConfigureAwait(false);
                if (stale.Found && stale.Value is not null)
                {
                    _diagnostics.RecordStaleCacheHit();
                    _logger.LogWarning(
                        exception,
                        "Использован устаревший дисковый кэш рецензий Kinopoisk ID {KinopoiskId}",
                        kinopoiskId);
                    return stale.Value;
                }

                throw;
            }
        }

        internal static string CreateCacheKey(int kinopoiskId, int page, string order)
            => $"presentation:reviews:{kinopoiskId}:{page}:{order}";
    }
}
