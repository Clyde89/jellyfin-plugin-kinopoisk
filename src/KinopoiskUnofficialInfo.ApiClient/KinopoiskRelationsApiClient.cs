using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Загружает связи фильмов через Kinopoisk Unofficial API.
    /// </summary>
    public sealed class KinopoiskRelationsApiClient : IKinopoiskRelationsApiClient
    {
        private const int MaximumAttempts = 3;
        private static readonly int[] TransientStatusCodes = { 408, 429, 500, 502, 503, 504 };
        private static readonly SemaphoreSlim RequestGate = new(1, 1);
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromSeconds(10);
        private static DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        private readonly Client _apiClient;
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

            var httpClient = httpClientFactory.CreateClient();
            httpClient.DefaultRequestHeaders.Add("X-API-KEY", apiToken.Trim());
            _apiClient = new Client(httpClient);
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
                    var result = await _apiClient
                        .PrequelsAsync(filmId, token)
                        .ConfigureAwait(false);
                    return result ?? Array.Empty<FilmSequelsAndPrequelsResponse>();
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
    }
}
