#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    /// <summary>
    /// Загружены похожие фильмы без передачи API-токена браузеру.
    /// </summary>
    public sealed class KinopoiskSimilarApiClient
    {
        private const string ApiBaseUrl = "https://kinopoiskapiunofficial.tech";
        private static readonly TimeSpan SuccessfulExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan EmptyExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly ILogger<KinopoiskSimilarApiClient> _logger;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        public KinopoiskSimilarApiClient(
            string apiToken,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            KinopoiskDiagnostics diagnostics,
            ILogger<KinopoiskSimilarApiClient> logger)
        {
            if (string.IsNullOrWhiteSpace(apiToken))
                throw new ArgumentException("API-токен не должен быть пустым.", nameof(apiToken));

            ArgumentNullException.ThrowIfNull(httpClientFactory);
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            _httpClient = httpClientFactory.CreateClient();
            _httpClient.DefaultRequestHeaders.Add("X-API-KEY", apiToken.Trim());
        }

        public async Task<KinopoiskSimilarResponse> GetSimilar(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                throw new ArgumentOutOfRangeException(nameof(kinopoiskId));

            var cacheKey = $"kinopoisk-presentation:similars:{kinopoiskId}";
            if (_cache.TryGetValue(cacheKey, out KinopoiskSimilarResponse? cached)
                && cached is not null)
            {
                return cached;
            }

            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_cache.TryGetValue(cacheKey, out cached) && cached is not null)
                    return cached;

                var delay = _nextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                _nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;

                _diagnostics.RecordApiRequest();
                using var request = new HttpRequestMessage(
                    HttpMethod.Get,
                    $"{ApiBaseUrl}/api/v2.2/films/{kinopoiskId}/similars");
                request.Headers.Accept.ParseAdd("application/json");

                try
                {
                    using var response = await _httpClient.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken)
                        .ConfigureAwait(false);

                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        var empty = new KinopoiskSimilarResponse();
                        _cache.Set(cacheKey, empty, EmptyExpiration);
                        RecordResult(kinopoiskId, empty);
                        return empty;
                    }

                    var json = await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"API КиноПоиска вернул HTTP {(int)response.StatusCode} для похожих фильмов.",
                            null,
                            response.StatusCode);
                    }

                    var result = MapResponse(json, kinopoiskId);
                    _cache.Set(
                        cacheKey,
                        result,
                        result.Items.Count > 0 ? SuccessfulExpiration : EmptyExpiration);
                    RecordResult(kinopoiskId, result);
                    return result;
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _diagnostics.RecordApiFailure();
                    _logger.LogWarning(
                        exception,
                        "Похожие фильмы для Kinopoisk ID {KinopoiskId} не загружены",
                        kinopoiskId);
                    throw;
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        internal static KinopoiskSimilarResponse MapResponse(string json, int sourceKinopoiskId)
        {
            var source = JsonConvert.DeserializeObject<SimilarWireResponse>(json)
                ?? new SimilarWireResponse();
            var items = source.Items
                .Where(item => item.FilmId > 0 && item.FilmId != sourceKinopoiskId)
                .Select(item => new KinopoiskSimilarInfo
                {
                    KinopoiskId = item.FilmId,
                    Name = KinopoiskPresentationTextSanitizer.NormalizePlainText(item.NameRu),
                    OriginalName = KinopoiskPresentationTextSanitizer.NormalizePlainText(
                        !string.IsNullOrWhiteSpace(item.NameOriginal)
                            ? item.NameOriginal
                            : item.NameEn),
                    PosterUrl = item.PosterUrl?.Trim() ?? string.Empty,
                    PosterUrlPreview = item.PosterUrlPreview?.Trim() ?? string.Empty,
                    KinopoiskUrl = $"https://www.kinopoisk.ru/film/{item.FilmId}/"
                })
                .GroupBy(item => item.KinopoiskId)
                .Select(group => group.First())
                .Take(20)
                .ToArray();

            return new KinopoiskSimilarResponse
            {
                Total = items.Length,
                Items = items
            };
        }

        private void RecordResult(int kinopoiskId, KinopoiskSimilarResponse result)
        {
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Debug,
                GetType().FullName ?? nameof(KinopoiskSimilarApiClient),
                "presentation.similars.completed",
                "Похожие фильмы КиноПоиска загружены.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kinopoiskId"] = kinopoiskId.ToString(CultureInfo.InvariantCulture),
                    ["total"] = result.Total.ToString(CultureInfo.InvariantCulture),
                    ["empty"] = (result.Total < 1).ToString(CultureInfo.InvariantCulture)
                });
        }

        private sealed class SimilarWireResponse
        {
            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("items")]
            public ICollection<SimilarWireItem> Items { get; set; }
                = Array.Empty<SimilarWireItem>();
        }

        private sealed class SimilarWireItem
        {
            [JsonProperty("filmId")]
            public int FilmId { get; set; }

            [JsonProperty("nameRu")]
            public string? NameRu { get; set; }

            [JsonProperty("nameEn")]
            public string? NameEn { get; set; }

            [JsonProperty("nameOriginal")]
            public string? NameOriginal { get; set; }

            [JsonProperty("posterUrl")]
            public string? PosterUrl { get; set; }

            [JsonProperty("posterUrlPreview")]
            public string? PosterUrlPreview { get; set; }
        }
    }
}
