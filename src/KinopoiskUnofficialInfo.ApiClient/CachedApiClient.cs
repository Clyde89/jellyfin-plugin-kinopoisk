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
    public class CachedKinopoiskApiClient : IFilteredKinopoiskApiClient, IKinopoiskImageApiClient
    {
        private static readonly TimeSpan PersonExpiration = TimeSpan.FromHours(24);
        private static readonly TimeSpan FilmExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan StaffExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan TrailersExpiration = TimeSpan.FromHours(6);
        private static readonly TimeSpan ImagesExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan SearchExpiration = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan EmptyResultExpiration = TimeSpan.FromMinutes(3);

        private readonly IKinopoiskApiClient _innerClient;
        private readonly IFilteredKinopoiskApiClient _filteredInnerClient;
        private readonly IKinopoiskImageApiClient _imageInnerClient;
        private readonly IMemoryCache _cache;
        private readonly ILogger<CachedKinopoiskApiClient> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Task<object>>> _inflightRequests = new();

        public CachedKinopoiskApiClient(IKinopoiskApiClient innerClient, IMemoryCache cache, ILogger<CachedKinopoiskApiClient> logger)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _filteredInnerClient = innerClient as IFilteredKinopoiskApiClient;
            _imageInnerClient = innerClient as IKinopoiskImageApiClient;
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public CachedKinopoiskApiClient(string apiToken, ILogger<KinopoiskApiClient> innerLogger, IHttpClientFactory httpClientFactory, IMemoryCache cache, ILogger<CachedKinopoiskApiClient> logger)
            : this(new KinopoiskApiClient(apiToken, innerLogger, httpClientFactory), cache, logger)
        {
        }

        public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetPerson), personId),
                PersonExpiration,
                EmptyResultExpiration,
                c => c.GetPerson(personId, CancellationToken.None),
                result => result is null,
                cancellationToken);

        public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetSingleFilm), filmId),
                FilmExpiration,
                EmptyResultExpiration,
                c => c.GetSingleFilm(filmId, CancellationToken.None),
                result => result is null,
                cancellationToken);

        public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetStaff), filmId),
                StaffExpiration,
                EmptyResultExpiration,
                c => c.GetStaff(filmId, CancellationToken.None),
                result => result is null || result.Count < 1,
                cancellationToken);

        public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(GetTrailers), filmId),
                TrailersExpiration,
                EmptyResultExpiration,
                c => c.GetTrailers(filmId, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
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
                ImagesExpiration,
                EmptyResultExpiration,
                _ => _imageInnerClient.GetImages(filmId, type, page, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
                cancellationToken);
        }

        public Task<FilmSearchResponse> SearchByKeyword(string keyword, int page = 1, CancellationToken? cancellationToken = null)
            => GetOrCreate(
                GenerateKey(nameof(SearchByKeyword), keyword, page),
                SearchExpiration,
                EmptyResultExpiration,
                c => c.SearchByKeyword(keyword, page, CancellationToken.None),
                result => result?.Films is null || result.Films.Count < 1,
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
                SearchExpiration,
                EmptyResultExpiration,
                _ => _filteredInnerClient.SearchFilms(query, CancellationToken.None),
                result => result?.Items is null || result.Items.Count < 1,
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
            TimeSpan emptyResultExpiration,
            Func<IKinopoiskApiClient, Task<T>> resultFactory,
            Func<T, bool> isEmptyResult,
            CancellationToken? cancellationToken)
        {
            var callerCancellationToken = cancellationToken ?? CancellationToken.None;
            callerCancellationToken.ThrowIfCancellationRequested();

            if (_cache.TryGetValue(key, out T cachedResult))
            {
                _logger.LogDebug("Ответ '{Key}' получен из кэша", key);
                return cachedResult;
            }

            var sharedRequest = _inflightRequests.GetOrAdd(
                key,
                _ => new Lazy<Task<object>>(
                    () => RequestAndCache(
                        key,
                        expiration,
                        emptyResultExpiration,
                        resultFactory,
                        isEmptyResult),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            return (T)await sharedRequest.Value
                .WaitAsync(callerCancellationToken)
                .ConfigureAwait(false);
        }

        private async Task<object> RequestAndCache<T>(
            string key,
            TimeSpan expiration,
            TimeSpan emptyResultExpiration,
            Func<IKinopoiskApiClient, Task<T>> resultFactory,
            Func<T, bool> isEmptyResult)
        {
            try
            {
                if (_cache.TryGetValue(key, out T cachedResult))
                    return cachedResult;

                _logger.LogDebug("Ответ '{Key}' отсутствует в кэше, выполнен запрос к серверу", key);
                var result = await resultFactory.Invoke(_innerClient).ConfigureAwait(false);
                var selectedExpiration = isEmptyResult(result)
                    ? emptyResultExpiration
                    : expiration;

                _cache.Set(key, result, selectedExpiration);

                _logger.LogDebug(
                    "Ответ '{Key}' сохранён в кэше на {ExpirationMinutes} минут",
                    key,
                    selectedExpiration.TotalMinutes);

                return result;
            }
            finally
            {
                _inflightRequests.TryRemove(key, out _);
            }
        }
    }
}
