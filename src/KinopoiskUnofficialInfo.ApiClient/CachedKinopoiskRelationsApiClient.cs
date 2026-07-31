using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Кэширует связи фильмов в памяти и долговременном хранилище.
    /// </summary>
    public sealed class CachedKinopoiskRelationsApiClient : IKinopoiskRelationsApiClient
    {
        private static readonly TimeSpan StaleMemoryExpiration = TimeSpan.FromMinutes(5);

        private readonly IKinopoiskRelationsApiClient _innerClient;
        private readonly IMemoryCache _memoryCache;
        private readonly KinopoiskCacheOptions _options;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly PersistentJsonCache _persistentCache;
        private readonly ILogger<CachedKinopoiskRelationsApiClient> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflightRequests = new();

        /// <summary>
        /// Инициализирует кэшируемый клиент связей фильмов.
        /// </summary>
        public CachedKinopoiskRelationsApiClient(
            IKinopoiskRelationsApiClient innerClient,
            IMemoryCache memoryCache,
            KinopoiskCacheOptions options,
            KinopoiskDiagnostics diagnostics,
            ILogger<CachedKinopoiskRelationsApiClient> logger)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _memoryCache = memoryCache ?? throw new ArgumentNullException(nameof(memoryCache));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (_options.EnablePersistentCache
                && !string.IsNullOrWhiteSpace(_options.PersistentCachePath))
            {
                _persistentCache = new PersistentJsonCache(
                    _options.PersistentCachePath,
                    _options.MaximumPersistentCacheBytes,
                    _logger);
            }
        }

        /// <inheritdoc />
        public async Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelations(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            if (filmId < 1)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(filmId),
                    filmId,
                    "Идентификатор КиноПоиска должен быть положительным.");
            }

            var token = cancellationToken ?? CancellationToken.None;
            token.ThrowIfCancellationRequested();
            var key = $"GetRelations;{filmId};";

            if (_memoryCache.TryGetValue(key, out ICollection<FilmSequelsAndPrequelsResponse> memoryResult))
            {
                _diagnostics.RecordMemoryCacheHit();
                return memoryResult;
            }

            var persistentResult = await ReadPersistent(key, allowStale: false, token)
                .ConfigureAwait(false);
            if (persistentResult.Found)
            {
                CachePersistentResult(key, persistentResult);
                _diagnostics.RecordPersistentCacheHit();
                return persistentResult.Value;
            }

            var sharedRequest = _inflightRequests.GetOrAdd(
                key,
                _ => new Lazy<Task<object>>(
                    () => RequestAndCache(key, filmId),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return (ICollection<FilmSequelsAndPrequelsResponse>)await sharedRequest.Value
                .WaitAsync(token)
                .ConfigureAwait(false);
        }

        private async Task<object> RequestAndCache(string key, int filmId)
        {
            try
            {
                if (_memoryCache.TryGetValue(
                    key,
                    out ICollection<FilmSequelsAndPrequelsResponse> memoryResult))
                {
                    _diagnostics.RecordMemoryCacheHit();
                    return memoryResult;
                }

                var persistentResult = await ReadPersistent(
                        key,
                        allowStale: false,
                        CancellationToken.None)
                    .ConfigureAwait(false);
                if (persistentResult.Found)
                {
                    CachePersistentResult(key, persistentResult);
                    _diagnostics.RecordPersistentCacheHit();
                    return persistentResult.Value;
                }

                try
                {
                    _diagnostics.RecordApiRequest();
                    var result = await _innerClient
                        .GetRelations(filmId, CancellationToken.None)
                        .ConfigureAwait(false)
                        ?? Array.Empty<FilmSequelsAndPrequelsResponse>();
                    var expiration = result.Count == 0
                        ? NormalizeExpiration(_options.EmptyResultExpiration, TimeSpan.FromMinutes(15))
                        : NormalizeExpiration(_options.MetadataExpiration, TimeSpan.FromDays(7));
                    var expiresAtUtc = DateTimeOffset.UtcNow + expiration;

                    _memoryCache.Set(key, result, expiration);
                    if (_persistentCache is not null)
                    {
                        var written = await _persistentCache
                            .Write(key, result, expiresAtUtc, CancellationToken.None)
                            .ConfigureAwait(false);
                        if (written)
                            _diagnostics.RecordPersistentCacheWrite();
                    }

                    return result;
                }
                catch (Exception exception)
                {
                    _diagnostics.RecordApiFailure();
                    if (_options.UseStaleCacheOnFailure && _persistentCache is not null)
                    {
                        var staleResult = await ReadPersistent(
                                key,
                                allowStale: true,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (staleResult.Found)
                        {
                            _memoryCache.Set(key, staleResult.Value, StaleMemoryExpiration);
                            _diagnostics.RecordStaleCacheHit();
                            _logger.LogWarning(
                                exception,
                                "Для связей Kinopoisk ID {KinopoiskId} использован устаревший дисковый кэш",
                                filmId);
                            return staleResult.Value;
                        }
                    }

                    throw;
                }
            }
            finally
            {
                _inflightRequests.TryRemove(key, out _);
            }
        }

        private Task<PersistentCacheReadResult<ICollection<FilmSequelsAndPrequelsResponse>>> ReadPersistent(
            string key,
            bool allowStale,
            CancellationToken cancellationToken)
        {
            if (_persistentCache is null)
            {
                return Task.FromResult(
                    PersistentCacheReadResult<ICollection<FilmSequelsAndPrequelsResponse>>.Miss);
            }

            return _persistentCache.Read<ICollection<FilmSequelsAndPrequelsResponse>>(
                key,
                allowStale,
                cancellationToken);
        }

        private void CachePersistentResult(
            string key,
            PersistentCacheReadResult<ICollection<FilmSequelsAndPrequelsResponse>> result)
        {
            var remaining = result.ExpiresAtUtc - DateTimeOffset.UtcNow;
            var expiration = result.IsStale || remaining <= TimeSpan.Zero
                ? StaleMemoryExpiration
                : remaining;
            _memoryCache.Set(key, result.Value, expiration);
        }

        private static TimeSpan NormalizeExpiration(TimeSpan value, TimeSpan fallback)
            => value > TimeSpan.Zero ? value : fallback;
    }
}
