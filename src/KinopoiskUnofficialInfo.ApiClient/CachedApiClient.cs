using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public class CachedKinopoiskApiClient : IFilteredKinopoiskApiClient, IKinopoiskImageApiClient, IKinopoiskPersonSearchApiClient, IKinopoiskSeasonApiClient
    {
        private static readonly TimeSpan TrailersExpiration = TimeSpan.FromHours(6);
        private static readonly TimeSpan StaleMemoryExpiration = TimeSpan.FromMinutes(5);

        private readonly IKinopoiskApiClient _innerClient;
        private readonly IFilteredKinopoiskApiClient _filteredInnerClient;
        private readonly IKinopoiskImageApiClient _imageInnerClient;
        private readonly IKinopoiskPersonSearchApiClient _personSearchInnerClient;
        private readonly IKinopoiskSeasonApiClient _seasonInnerClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CachedKinopoiskApiClient> _logger;
        private readonly KinopoiskCacheOptions _options;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly PersistentJsonCache _persistentCache;
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflightRequests = new();

        public CachedKinopoiskApiClient(
            IKinopoiskApiClient innerClient,
            IMemoryCache cache,
            ILogger<CachedKinopoiskApiClient> logger)
            : this(
                innerClient,
                cache,
                logger,
                new KinopoiskCacheOptions(),
                KinopoiskDiagnostics.Shared)
        {
        }

        public CachedKinopoiskApiClient(
            IKinopoiskApiClient innerClient,
            IMemoryCache cache,
            ILogger<CachedKinopoiskApiClient> logger,
            KinopoiskCacheOptions options,
            KinopoiskDiagnostics diagnostics)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _filteredInnerClient = innerClient as IFilteredKinopoiskApiClient;
            _imageInnerClient = innerClient as IKinopoiskImageApiClient;
            _personSearchInnerClient = innerClient as IKinopoiskPersonSearchApiClient;
            _seasonInnerClient = innerClient as IKinopoiskSeasonApiClient;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));

            if (_options.EnablePersistentCache
                && !string.IsNullOrWhiteSpace(_options.PersistentCachePath))
            {
                _persistentCache = new PersistentJsonCache(
                    _options.PersistentCachePath,
                    _options.MaximumPersistentCacheBytes,
                    _logger);
            }
        }

        public CachedKinopoiskApiClient(
            string apiToken,
            ILogger<KinopoiskApiClient> innerLogger,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<CachedKinopoiskApiClient> logger)
            : this(
                new KinopoiskApiClient(apiToken, innerLogger, httpClientFactory),
                cache,
                logger)
        {
        }

        public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetPerson), personId),
                NormalizeExpiration(_options.MetadataExpiration, TimeSpan.FromHours(12)),
                c => c.GetPerson(personId, CancellationToken.None),
                result => result is null,
                persist: true,
                cancellationToken);

        public Task<PersonSearchResponse> SearchPersons(
            string name,
            int page = 1,
            CancellationToken? cancellationToken = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Имя персоны не должно быть пустым.", nameof(name));

            if (_personSearchInnerClient is null)
                throw new NotSupportedException("Клиент КиноПоиска не поддерживает поиск персон.");

            var normalizedName = name.Trim();
            var selectedPage = page is >= 1 and <= 2 ? page : 1;
            return GetOrCreate(
                GenerateKey(nameof(SearchPersons), normalizedName.ToUpperInvariant(), selectedPage),
                NormalizeExpiration(_options.SearchExpiration, TimeSpan.FromMinutes(15)),
                _ => _personSearchInnerClient.SearchPersons(
                    normalizedName,
                    selectedPage,
                    CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                persist: true,
                cancellationToken);
        }

        public Task<SeasonResponse> GetSeasons(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            if (filmId < 1)
                throw new ArgumentOutOfRangeException(nameof(filmId), filmId, "Идентификатор КиноПоиска должен быть положительным.");

            if (_seasonInnerClient is null)
                throw new NotSupportedException("Клиент КиноПоиска не поддерживает получение сезонов.");

            return GetOrCreate(
                GenerateKey(nameof(GetSeasons), filmId),
                NormalizeExpiration(_options.MetadataExpiration, TimeSpan.FromHours(12)),
                _ => _seasonInnerClient.GetSeasons(filmId, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                persist: true,
                cancellationToken);
        }

        public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetSingleFilm), filmId),
                NormalizeExpiration(_options.MetadataExpiration, TimeSpan.FromHours(12)),
                c => c.GetSingleFilm(filmId, CancellationToken.None),
                result => result is null,
                persist: true,
                cancellationToken);

        public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetStaff), filmId),
                NormalizeExpiration(_options.MetadataExpiration, TimeSpan.FromHours(12)),
                c => c.GetStaff(filmId, CancellationToken.None),
                result => result is null || result.Count < 1,
                persist: true,
                cancellationToken);

        public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetTrailers), filmId),
                TrailersExpiration,
                c => c.GetTrailers(filmId, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                persist: false,
                cancellationToken);

        public Task<ImageResponse> GetImages(
            int filmId,
            FilmImageType type,
            int page = 1,
            CancellationToken? cancellationToken = null)
        {
            if (_imageInnerClient is null)
                throw new NotSupportedException("Клиент КиноПоиска не поддерживает получение расширенных изображений.");

            return GetOrCreate(
                GenerateKey(nameof(GetImages), filmId, type, page),
                NormalizeExpiration(_options.ImagesExpiration, TimeSpan.FromHours(12)),
                _ => _imageInnerClient.GetImages(filmId, type, page, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                persist: true,
                cancellationToken);
        }

        public Task<FilmSearchResponse> SearchByKeyword(
            string keyword,
            int page = 1,
            CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(SearchByKeyword), keyword, page),
                NormalizeExpiration(_options.SearchExpiration, TimeSpan.FromMinutes(15)),
                c => c.SearchByKeyword(keyword, page, CancellationToken.None),
                result => result?.Films is null || result.Films.Count < 1,
                persist: true,
                cancellationToken);

        public Task<FilteredFilmSearchResponse> SearchFilms(
            FilmSearchQuery query,
            CancellationToken? cancellationToken = null)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));

            if (_filteredInnerClient is null)
                throw new NotSupportedException("Клиент КиноПоиска не поддерживает фильтрованный поиск.");

            return GetOrCreate(
                GenerateKey(nameof(SearchFilms), query),
                NormalizeExpiration(_options.SearchExpiration, TimeSpan.FromMinutes(15)),
                _ => _filteredInnerClient.SearchFilms(query, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                persist: true,
                cancellationToken);
        }

        private static string GenerateKey(params object[] objects)
        {
            var key = string.Empty;

            foreach (var obj in objects)
            {
                var objType = obj.GetType();
                if (objType.IsPrimitive || objType == typeof(string) || objType.IsEnum)
                {
                    key += obj + ";";
                }
                else
                {
                    foreach (PropertyInfo propertyInfo in objType.GetProperties())
                    {
                        var currentValue = propertyInfo.GetValue(obj, null);
                        if (currentValue == null)
                            continue;

                        key += propertyInfo.Name + "=" + currentValue + ";";
                    }
                }
            }

            return key;
        }

        private async Task<T> GetOrCreate<T>(
            string key,
            TimeSpan expiration,
            Func<IKinopoiskApiClient, Task<T>> resultFactory,
            Func<T, bool> isEmptyResult,
            bool persist,
            CancellationToken? cancellationToken)
        {
            var callerCancellationToken = cancellationToken ?? CancellationToken.None;
            callerCancellationToken.ThrowIfCancellationRequested();

            if (_cache.TryGetValue(key, out T cachedResult))
            {
                _diagnostics.RecordMemoryCacheHit();
                _logger.LogDebug("Ответ '{Key}' получен из оперативного кэша", key);
                return cachedResult;
            }

            var persistentResult = await ReadPersistent<T>(
                    key,
                    allowStale: false,
                    persist,
                    callerCancellationToken)
                .ConfigureAwait(false);
            if (persistentResult.Found)
            {
                CachePersistentResult(key, persistentResult);
                _diagnostics.RecordPersistentCacheHit();
                _logger.LogDebug("Ответ '{Key}' получен из долговременного дискового кэша", key);
                return persistentResult.Value;
            }

            var sharedRequest = _inflightRequests.GetOrAdd(
                key,
                _ => new Lazy<Task<object>>(
                    () => RequestAndCache(
                        key,
                        expiration,
                        resultFactory,
                        isEmptyResult,
                        persist),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return (T)await sharedRequest.Value
                .WaitAsync(callerCancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<object> RequestAndCache<T>(
            string key,
            TimeSpan expiration,
            Func<IKinopoiskApiClient, Task<T>> resultFactory,
            Func<T, bool> isEmptyResult,
            bool persist)
        {
            try
            {
                if (_cache.TryGetValue(key, out T cachedResult))
                {
                    _diagnostics.RecordMemoryCacheHit();
                    return cachedResult;
                }

                var persistentResult = await ReadPersistent<T>(
                        key,
                        allowStale: false,
                        persist,
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
                    _logger.LogDebug("Ответ '{Key}' отсутствует в кэше, выполнен запрос к серверу", key);
                    _diagnostics.RecordApiRequest();
                    var result = await resultFactory.Invoke(_innerClient).ConfigureAwait(false);
                    var selectedExpiration = isEmptyResult(result)
                        ? NormalizeExpiration(_options.EmptyResultExpiration, TimeSpan.FromMinutes(3))
                        : expiration;
                    var expiresAtUtc = DateTimeOffset.UtcNow + selectedExpiration;

                    _cache.Set(key, result, selectedExpiration);

                    if (_persistentCache is not null && persist)
                    {
                        var written = await _persistentCache.Write(
                                key,
                                result,
                                expiresAtUtc,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (written)
                            _diagnostics.RecordPersistentCacheWrite();
                    }

                    _logger.LogDebug(
                        "Ответ '{Key}' сохранён в кэше на {ExpirationMinutes} минут",
                        key,
                        selectedExpiration.TotalMinutes);

                    return result;
                }
                catch (Exception exception)
                {
                    _diagnostics.RecordApiFailure();

                    if (_options.UseStaleCacheOnFailure && _persistentCache is not null && persist)
                    {
                        var staleResult = await ReadPersistent<T>(
                                key,
                                allowStale: true,
                                persist: true,
                                CancellationToken.None)
                            .ConfigureAwait(false);
                        if (staleResult.Found)
                        {
                            _cache.Set(key, staleResult.Value, StaleMemoryExpiration);
                            _diagnostics.RecordStaleCacheHit();
                            _logger.LogWarning(
                                exception,
                                "Для ответа '{Key}' использован устаревший дисковый кэш из-за ошибки API",
                                key);
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

        private Task<PersistentCacheReadResult<T>> ReadPersistent<T>(
            string key,
            bool allowStale,
            bool persist,
            CancellationToken cancellationToken)
        {
            if (_persistentCache is null || !persist)
                return Task.FromResult(PersistentCacheReadResult<T>.Miss);

            return _persistentCache.Read<T>(key, allowStale, cancellationToken);
        }

        private void CachePersistentResult<T>(string key, PersistentCacheReadResult<T> result)
        {
            var remaining = result.ExpiresAtUtc - DateTimeOffset.UtcNow;
            var expiration = result.IsStale || remaining <= TimeSpan.Zero
                ? StaleMemoryExpiration
                : remaining;
            _cache.Set(key, result.Value, expiration);
        }

        private static TimeSpan NormalizeExpiration(TimeSpan value, TimeSpan fallback)
            => value > TimeSpan.Zero ? value : fallback;
    }
}
