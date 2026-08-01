using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Загружает связи фильмов через актуальный endpoint Kinopoisk Unofficial API.
    /// </summary>
    public sealed class KinopoiskRelationsApiClient : IKinopoiskRelationsApiClient
    {
        private const string ApiBaseUrl = "https://kinopoiskapiunofficial.tech";
        private const int MaximumAttempts = 3;
        private static readonly int[] TransientStatusCodes = { 408, 429, 500, 502, 503, 504 };
        private static readonly SemaphoreSlim RequestGate = new(1, 1);
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);
        private static readonly JsonSerializerSettings SerializerSettings =
            JsonTransformator.TransformSettings(new JsonSerializerSettings());
        private static DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        private readonly HttpClient _httpClient;
        private readonly ILogger<KinopoiskRelationsApiClient> _logger;

        /// <summary>
        /// Инициализирует клиент связей фильмов.
        /// </summary>
        public KinopoiskRelationsApiClient(
            string apiToken,
            IHttpClientFactory httpClientFactory,
            ILogger<KinopoiskRelationsApiClient> logger)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API-токен не должен быть пустым.", nameof(apiToken));

            ArgumentNullException.ThrowIfNull(httpClientFactory);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _httpClient = httpClientFactory.CreateClient();
            _httpClient.DefaultRequestHeaders.Add("X-API-KEY", apiToken.Trim());
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
            for (var attempt = 1; attempt <= MaximumAttempts; attempt++)
            {
                token.ThrowIfCancellationRequested();
                await WaitForRequestWindow(token).ConfigureAwait(false);

                try
                {
                    return await SendRequest(filmId, token).ConfigureAwait(false);
                }
                catch (ApiException exception) when (exception.StatusCode == 404)
                {
                    return Array.Empty<FilmSequelsAndPrequelsResponse>();
                }
                catch (ApiException exception) when (
                    IsTransientStatusCode(exception.StatusCode)
                    && attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(
                            filmId,
                            attempt,
                            exception.StatusCode,
                            exception.Headers,
                            token)
                        .ConfigureAwait(false);
                }
                catch (HttpRequestException) when (attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(filmId, attempt, null, null, token)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (
                    !token.IsCancellationRequested
                    && attempt < MaximumAttempts)
                {
                    await WaitBeforeRetry(filmId, attempt, null, null, token)
                        .ConfigureAwait(false);
                }
            }

            throw new InvalidOperationException(
                "Повторные попытки загрузки связей КиноПоиска завершены без результата.");
        }

        private async Task<ICollection<FilmSequelsAndPrequelsResponse>> SendRequest(
            int filmId,
            CancellationToken cancellationToken)
        {
            var requestUri = $"{ApiBaseUrl}/api/v2.2/films/{filmId}/relations";
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotFound)
                return Array.Empty<FilmSequelsAndPrequelsResponse>();

            var json = await response.Content
                .ReadAsStringAsync(cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                var headers = response.Headers.ToDictionary(item => item.Key, item => item.Value);
                foreach (var header in response.Content.Headers)
                    headers[header.Key] = header.Value;

                throw new ApiException(
                    "Сервер КиноПоиска вернул неожиданный HTTP-статус.",
                    (int)response.StatusCode,
                    json,
                    headers,
                    null);
            }

            var payload = JsonConvert.DeserializeObject<RelationsWireResponse>(
                    json,
                    SerializerSettings)
                ?? new RelationsWireResponse();

            var result = payload.Items
                .Select(MapRelation)
                .Where(item => item is not null)
                .ToArray();

            _logger.LogDebug(
                "Для Kinopoisk ID {KinopoiskId} получено связей: {ReceivedCount}, поддержано франшизных связей: {SelectedCount}",
                filmId,
                payload.Items.Count,
                result.Length);

            return result;
        }

        private static FilmSequelsAndPrequelsResponse MapRelation(RelationWireItem source)
        {
            if (source is null || source.FilmId < 1)
                return null;

            if (!TryMapRelationType(source.RelationType, out var relationType))
                return null;

            return new FilmSequelsAndPrequelsResponse
            {
                FilmId = source.FilmId,
                NameRu = source.NameRu ?? string.Empty,
                NameEn = source.NameEn ?? string.Empty,
                NameOriginal = source.NameOriginal ?? string.Empty,
                PosterUrl = source.PosterUrl ?? string.Empty,
                PosterUrlPreview = source.PosterUrlPreview ?? string.Empty,
                RelationType = relationType
            };
        }

        private static bool TryMapRelationType(
            string value,
            out FilmSequelsAndPrequelsResponseRelationType relationType)
        {
            relationType = FilmSequelsAndPrequelsResponseRelationType.UNKNOWN;
            if (string.IsNullOrWhiteSpace(value))
                return false;

            switch (value.Trim().ToUpperInvariant())
            {
                case "SEQUEL":
                    relationType = FilmSequelsAndPrequelsResponseRelationType.SEQUEL;
                    return true;
                case "PREQUEL":
                    relationType = FilmSequelsAndPrequelsResponseRelationType.PREQUEL;
                    return true;
                case "REMAKE":
                    relationType = FilmSequelsAndPrequelsResponseRelationType.REMAKE;
                    return true;
                default:
                    return false;
            }
        }

        private static bool IsTransientStatusCode(int statusCode)
            => Array.IndexOf(TransientStatusCodes, statusCode) >= 0;

        private static async Task WaitForRequestWindow(CancellationToken cancellationToken)
        {
            await RequestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var delay = _nextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                _nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;
            }
            finally
            {
                RequestGate.Release();
            }
        }

        private async Task WaitBeforeRetry(
            int filmId,
            int completedAttempt,
            int? statusCode,
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            CancellationToken cancellationToken)
        {
            var delay = GetRetryDelay(headers, completedAttempt);
            _logger.LogWarning(
                "Загрузка связей Kinopoisk ID {KinopoiskId} временно не выполнена со статусом {StatusCode}; попытка {NextAttempt}/{MaximumAttempts} выполнена через {DelayMilliseconds} мс",
                filmId,
                statusCode,
                completedAttempt + 1,
                MaximumAttempts,
                delay.TotalMilliseconds);
            await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
        }

        private static TimeSpan GetRetryDelay(
            IReadOnlyDictionary<string, IEnumerable<string>> headers,
            int completedAttempt)
        {
            if (headers is not null)
            {
                var value = headers
                    .FirstOrDefault(item => string.Equals(
                        item.Key,
                        "Retry-After",
                        StringComparison.OrdinalIgnoreCase))
                    .Value?
                    .FirstOrDefault();
                if (int.TryParse(value, out var seconds))
                    return TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10));
            }

            var delay = TimeSpan.FromMilliseconds(
                (250 * Math.Pow(2, completedAttempt - 1)) + Random.Shared.Next(50, 151));
            return delay > MaximumRetryDelay ? MaximumRetryDelay : delay;
        }

        private sealed class RelationsWireResponse
        {
            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("items")]
            public ICollection<RelationWireItem> Items { get; set; }
                = Array.Empty<RelationWireItem>();
        }

        private sealed class RelationWireItem
        {
            [JsonProperty("filmId")]
            public int FilmId { get; set; }

            [JsonProperty("nameRu")]
            public string NameRu { get; set; }

            [JsonProperty("nameEn")]
            public string NameEn { get; set; }

            [JsonProperty("nameOriginal")]
            public string NameOriginal { get; set; }

            [JsonProperty("posterUrl")]
            public string PosterUrl { get; set; }

            [JsonProperty("posterUrlPreview")]
            public string PosterUrlPreview { get; set; }

            [JsonProperty("relationType")]
            public string RelationType { get; set; }
        }
    }
}
