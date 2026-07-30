using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public class KinopoiskApiClient : IFilteredKinopoiskApiClient, IKinopoiskImageApiClient, IKinopoiskPersonSearchApiClient, IKinopoiskSeasonApiClient
    {
        private const string ApiBaseUrl = "https://kinopoiskapiunofficial.tech";
        private const int MaximumAttempts = 3;
        private static readonly int[] TransientStatusCodes = { 408, 429, 500, 502, 503, 504 };
        private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(200);

        private readonly string _apiToken;
        private readonly ILogger<KinopoiskApiClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _httpClient;
        private readonly Client _apiClient;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        public KinopoiskApiClient(string apiToken, ILogger<KinopoiskApiClient> logger, IHttpClientFactory httpClientFactory)
        {
            if (string.IsNullOrEmpty(apiToken))
            {
                throw new ArgumentException($"'{nameof(apiToken)}' cannot be null or empty.", nameof(apiToken));
            }

            _apiToken = apiToken;
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));

            _httpClient = _httpClientFactory.CreateClient();
            _httpClient.DefaultRequestHeaders.Add("X-API-KEY", _apiToken);
            _apiClient = new Client(_httpClient);
        }

        private async Task<T> Invoke<T>(
            Func<CancellationToken, Task<T>> method,
            CancellationToken? ct,
            [CallerMemberName] string memberName = "")
        {
            var cancellationToken = ct ?? CancellationToken.None;

            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await WaitForRequestWindow(cancellationToken).ConfigureAwait(false);

                try
                {
                    _logger.LogDebug(
                        "Запрос {MemberName} начат, попытка {Attempt}/{MaximumAttempts}",
                        memberName,
                        attempt,
                        MaximumAttempts);

                    var result = await method.Invoke(cancellationToken).ConfigureAwait(false);

                    _logger.LogDebug(
                        "Запрос {MemberName} успешно завершён, попытка {Attempt}/{MaximumAttempts}",
                        memberName,
                        attempt,
                        MaximumAttempts);

                    return result;
                }
                catch (ApiException exception) when (
                    IsTransientStatusCode(exception.StatusCode)
                    && attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(
                        memberName,
                        attempt,
                        exception.StatusCode,
                        exception.Headers,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(
                        memberName,
                        attempt,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested
                    && attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(
                        memberName,
                        attempt,
                        null,
                        null,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (ApiException exception)
                {
                    _logger.LogError(
                        "Запрос {MemberName} завершён ошибкой КиноПоиска со статусом {StatusCode}",
                        memberName,
                        exception.StatusCode);
                    throw;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogError(
                        "Запрос {MemberName} завершён по тайм-ауту после {MaximumAttempts} попыток",
                        memberName,
                        MaximumAttempts);
                    throw;
                }
                catch (HttpRequestException exception)
                {
                    _logger.LogError(
                        exception,
                        "Запрос {MemberName} завершён сетевой ошибкой после {MaximumAttempts} попыток",
                        memberName,
                        MaximumAttempts);
                    throw;
                }
            }

            throw new InvalidOperationException("Повторные попытки запроса КиноПоиска завершены без результата.");
        }

        public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            => Invoke((ct) => _apiClient.FilmsAsync(filmId, ct), cancellationToken);

        public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
            => Invoke((ct) => _apiClient.StaffAllAsync(filmId, ct), cancellationToken);

        public Task<FilmSearchResponse> SearchByKeyword(string keyword, int page = 1, CancellationToken? cancellationToken = null)
            => Invoke((ct) => _apiClient.SearchByKeywordAsync(keyword, page, ct), cancellationToken);

        public Task<FilteredFilmSearchResponse> SearchFilms(
            FilmSearchQuery query,
            CancellationToken? cancellationToken = null)
        {
            if (query is null)
                throw new ArgumentNullException(nameof(query));

            return Invoke((ct) => SearchFilmsCore(query, ct), cancellationToken);
        }

        public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
            => Invoke((ct) => _apiClient.StaffAsync(personId, ct), cancellationToken);

        public Task<PersonSearchResponse> SearchPersons(
            string name,
            int page = 1,
            CancellationToken? cancellationToken = null)
        {
            if (string.IsNullOrWhiteSpace(name))
                throw new ArgumentException("Имя персоны не должно быть пустым.", nameof(name));

            var selectedPage = page is >= 1 and <= 2 ? page : 1;
            return Invoke(
                ct => SearchPersonsCore(name.Trim(), selectedPage, ct),
                cancellationToken);
        }

        public Task<SeasonResponse> GetSeasons(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            if (filmId < 1)
                throw new ArgumentOutOfRangeException(nameof(filmId), filmId, "Идентификатор КиноПоиска должен быть положительным.");

            return Invoke(
                ct => _apiClient.SeasonsAsync(filmId, ct),
                cancellationToken);
        }

        public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
        {
            return Invoke(async (ct) => {
                try {
                    return await _apiClient.VideosAsync(filmId, ct).ConfigureAwait(false);
                } catch (ApiException e)
                {
                    if (e.StatusCode == 404)
                        return new VideoResponse();
                    throw;
                }
            }, cancellationToken);
        }

        public Task<ImageResponse> GetImages(
            int filmId,
            FilmImageType type,
            int page = 1,
            CancellationToken? cancellationToken = null)
        {
            if (filmId < 1)
                throw new ArgumentOutOfRangeException(nameof(filmId), filmId, "Идентификатор КиноПоиска должен быть положительным.");

            var selectedPage = page is >= 1 and <= 20 ? page : 1;
            return Invoke(
                ct => GetImagesCore(filmId, type, selectedPage, ct),
                cancellationToken);
        }

        private async Task<FilteredFilmSearchResponse> SearchFilmsCore(
            FilmSearchQuery query,
            CancellationToken cancellationToken)
        {
            var parameters = new List<string>();

            AddParameter(parameters, "imdbId", query.ImdbId);
            AddParameter(parameters, "keyword", query.Keyword);

            if (query.YearFrom.HasValue)
                AddParameter(parameters, "yearFrom", query.YearFrom.Value.ToString(CultureInfo.InvariantCulture));

            if (query.YearTo.HasValue)
                AddParameter(parameters, "yearTo", query.YearTo.Value.ToString(CultureInfo.InvariantCulture));

            AddParameter(parameters, "type", query.Type);

            var page = query.Page is >= 1 and <= 20 ? query.Page : 1;
            AddParameter(parameters, "page", page.ToString(CultureInfo.InvariantCulture));

            var requestUri = $"{ApiBaseUrl}/api/v2.2/films?{string.Join("&", parameters)}";
            return await SendJsonRequest<FilteredFilmSearchResponse>(requestUri, cancellationToken)
                .ConfigureAwait(false);
        }

        private Task<PersonSearchResponse> SearchPersonsCore(
            string name,
            int page,
            CancellationToken cancellationToken)
        {
            var requestUri = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/api/v1/persons?name={1}&page={2}",
                ApiBaseUrl,
                Uri.EscapeDataString(name),
                page);

            return SendJsonRequest<PersonSearchResponse>(requestUri, cancellationToken);
        }

        private Task<ImageResponse> GetImagesCore(
            int filmId,
            FilmImageType type,
            int page,
            CancellationToken cancellationToken)
        {
            var requestUri = string.Format(
                CultureInfo.InvariantCulture,
                "{0}/api/v2.2/films/{1}/images?type={2}&page={3}",
                ApiBaseUrl,
                filmId,
                Uri.EscapeDataString(type.ToString()),
                page);

            return SendJsonRequest<ImageResponse>(requestUri, cancellationToken);
        }

        private async Task<T> SendJsonRequest<T>(
            string requestUri,
            CancellationToken cancellationToken)
            where T : new()
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken).ConfigureAwait(false);

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var headers = response.Headers.ToDictionary(header => header.Key, header => header.Value);
                foreach (var header in response.Content.Headers)
                    headers[header.Key] = header.Value;

                throw new ApiException(
                    "The HTTP status code of the response was not expected.",
                    (int)response.StatusCode,
                    responseText,
                    headers,
                    null);
            }

            return JsonConvert.DeserializeObject<T>(responseText) ?? new T();
        }

        private async Task WaitForRequestWindow(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var delay = _nextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                _nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private async Task WaitBeforeRetry(
            string memberName,
            int completedAttempt,
            int? statusCode,
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            CancellationToken cancellationToken)
        {
            var delay = GetRetryDelay(headers, completedAttempt);

            if (statusCode.HasValue)
            {
                _logger.LogWarning(
                    "Запрос {MemberName} получил временный статус {StatusCode}; повторная попытка {NextAttempt}/{MaximumAttempts} выполнена через {DelayMilliseconds} мс",
                    memberName,
                    statusCode.Value,
                    completedAttempt + 1,
                    MaximumAttempts,
                    delay.TotalMilliseconds);
            }
            else
            {
                _logger.LogWarning(
                    "Запрос {MemberName} завершён временной сетевой ошибкой; повторная попытка {NextAttempt}/{MaximumAttempts} выполнена через {DelayMilliseconds} мс",
                    memberName,
                    completedAttempt + 1,
                    MaximumAttempts,
                    delay.TotalMilliseconds);
            }

            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private static bool IsTransientStatusCode(int statusCode)
        {
            return Array.IndexOf(TransientStatusCodes, statusCode) >= 0;
        }

        private static TimeSpan GetRetryDelay(
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            int completedAttempt)
        {
            if (TryGetRetryAfter(headers, out var retryAfter))
                return retryAfter > MaximumRetryDelay ? MaximumRetryDelay : retryAfter;

            var exponentialMilliseconds = 250 * Math.Pow(2, completedAttempt - 1);
            var jitterMilliseconds = Random.Shared.Next(50, 151);
            var calculatedDelay = TimeSpan.FromMilliseconds(exponentialMilliseconds + jitterMilliseconds);

            return calculatedDelay > MaximumRetryDelay
                ? MaximumRetryDelay
                : calculatedDelay;
        }

        private static bool TryGetRetryAfter(
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            out TimeSpan retryAfter)
        {
            retryAfter = TimeSpan.Zero;
            if (headers is null)
                return false;

            var header = headers.FirstOrDefault(item =>
                string.Equals(item.Key, "Retry-After", StringComparison.OrdinalIgnoreCase));
            var value = header.Value?.FirstOrDefault();

            if (string.IsNullOrWhiteSpace(value))
                return false;

            if (int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds))
            {
                retryAfter = TimeSpan.FromSeconds(Math.Max(0, seconds));
                return true;
            }

            if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal,
                out var retryDate))
            {
                return false;
            }

            retryAfter = retryDate - DateTimeOffset.UtcNow;
            if (retryAfter < TimeSpan.Zero)
                retryAfter = TimeSpan.Zero;

            return true;
        }

        private static void AddParameter(ICollection<string> parameters, string name, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            parameters.Add(
                $"{Uri.EscapeDataString(name)}={Uri.EscapeDataString(value.Trim())}");
        }
    }
}
