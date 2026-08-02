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
    /// Загружает факты, бюджет, сборы и награды без передачи API-токена браузеру.
    /// </summary>
    public sealed class KinopoiskSupplementalApiClient
    {
        private const string ApiBaseUrl = "https://kinopoiskapiunofficial.tech";
        private static readonly TimeSpan SuccessfulExpiration = TimeSpan.FromHours(12);
        private static readonly TimeSpan EmptyExpiration = TimeSpan.FromMinutes(30);
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(250);

        private readonly HttpClient _httpClient;
        private readonly IMemoryCache _cache;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly ILogger<KinopoiskSupplementalApiClient> _logger;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        public KinopoiskSupplementalApiClient(
            string apiToken,
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            KinopoiskDiagnostics diagnostics,
            ILogger<KinopoiskSupplementalApiClient> logger)
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

        public Task<KinopoiskFactsResponse> GetFacts(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => GetOrCreate(
                kinopoiskId,
                "facts",
                $"/api/v2.2/films/{kinopoiskId}/facts",
                MapFacts,
                result => result.Total < 1,
                cancellationToken);

        public Task<KinopoiskBoxOfficeResponse> GetBoxOffice(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => GetOrCreate(
                kinopoiskId,
                "box-office",
                $"/api/v2.2/films/{kinopoiskId}/box_office",
                MapBoxOffice,
                result => result.Total < 1,
                cancellationToken);

        public Task<KinopoiskAwardsResponse> GetAwards(
            int kinopoiskId,
            CancellationToken cancellationToken)
            => GetOrCreate(
                kinopoiskId,
                "awards",
                $"/api/v2.2/films/{kinopoiskId}/awards",
                MapAwards,
                result => result.Total < 1,
                cancellationToken);

        private async Task<T> GetOrCreate<T>(
            int kinopoiskId,
            string section,
            string path,
            Func<string, T> mapper,
            Func<T, bool> isEmpty,
            CancellationToken cancellationToken)
            where T : class
        {
            if (kinopoiskId < 1)
                throw new ArgumentOutOfRangeException(nameof(kinopoiskId));

            var cacheKey = $"kinopoisk-presentation:{section}:{kinopoiskId}";
            if (_cache.TryGetValue(cacheKey, out T? cached) && cached is not null)
                return cached;

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
                using var request = new HttpRequestMessage(HttpMethod.Get, ApiBaseUrl + path);
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
                        var empty = mapper("{}");
                        _cache.Set(cacheKey, empty, EmptyExpiration);
                        RecordResult(kinopoiskId, section, 0, true);
                        return empty;
                    }

                    var json = await response.Content
                        .ReadAsStringAsync(cancellationToken)
                        .ConfigureAwait(false);
                    if (!response.IsSuccessStatusCode)
                    {
                        throw new HttpRequestException(
                            $"API КиноПоиска вернул HTTP {(int)response.StatusCode} для раздела {section}.",
                            null,
                            response.StatusCode);
                    }

                    var result = mapper(json);
                    var emptyResult = isEmpty(result);
                    _cache.Set(
                        cacheKey,
                        result,
                        emptyResult ? EmptyExpiration : SuccessfulExpiration);
                    RecordResult(kinopoiskId, section, GetTotal(result), emptyResult);
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
                        "Дополнительный раздел {Section} для Kinopoisk ID {KinopoiskId} не загружен",
                        section,
                        kinopoiskId);
                    throw;
                }
            }
            finally
            {
                _requestGate.Release();
            }
        }

        private void RecordResult(int kinopoiskId, string section, int total, bool empty)
        {
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Debug,
                GetType().FullName ?? nameof(KinopoiskSupplementalApiClient),
                "presentation.supplemental.completed",
                "Дополнительный раздел карточки КиноПоиска загружен.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kinopoiskId"] = kinopoiskId.ToString(CultureInfo.InvariantCulture),
                    ["section"] = section,
                    ["total"] = total.ToString(CultureInfo.InvariantCulture),
                    ["empty"] = empty.ToString(CultureInfo.InvariantCulture)
                });
        }

        private static int GetTotal<T>(T result)
            => result switch
            {
                KinopoiskFactsResponse facts => facts.Total,
                KinopoiskBoxOfficeResponse boxOffice => boxOffice.Total,
                KinopoiskAwardsResponse awards => awards.Total,
                _ => 0
            };

        private static KinopoiskFactsResponse MapFacts(string json)
        {
            var source = JsonConvert.DeserializeObject<FactsWireResponse>(json)
                ?? new FactsWireResponse();
            var items = source.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Text))
                .Select(item => new KinopoiskFactInfo
                {
                    Text = item.Text!.Trim(),
                    Type = item.Type ?? string.Empty,
                    Spoiler = item.Spoiler
                })
                .ToArray();
            return new KinopoiskFactsResponse
            {
                Total = source.Total > 0 ? source.Total : items.Length,
                Items = items
            };
        }

        private static KinopoiskBoxOfficeResponse MapBoxOffice(string json)
        {
            var source = JsonConvert.DeserializeObject<BoxOfficeWireResponse>(json)
                ?? new BoxOfficeWireResponse();
            var items = source.Items
                .Where(item => item.Amount != 0 || !string.IsNullOrWhiteSpace(item.Type))
                .Select(item => new KinopoiskBoxOfficeInfo
                {
                    Type = item.Type ?? string.Empty,
                    Amount = item.Amount,
                    Currency = item.Currency ?? string.Empty,
                    Symbol = item.Symbol ?? string.Empty
                })
                .ToArray();
            return new KinopoiskBoxOfficeResponse
            {
                Total = source.Total > 0 ? source.Total : items.Length,
                Items = items
            };
        }

        private static KinopoiskAwardsResponse MapAwards(string json)
        {
            var source = JsonConvert.DeserializeObject<AwardsWireResponse>(json)
                ?? new AwardsWireResponse();
            var items = source.Items
                .Where(item => !string.IsNullOrWhiteSpace(item.Name)
                    || !string.IsNullOrWhiteSpace(item.NominationName))
                .Select(item => new KinopoiskAwardInfo
                {
                    Name = item.Name ?? string.Empty,
                    NominationName = item.NominationName ?? string.Empty,
                    Year = item.Year,
                    Win = item.Win,
                    ImageUrl = item.ImageUrl ?? string.Empty,
                    Persons = (item.Persons ?? Array.Empty<AwardPersonWireItem>())
                        .Select(person => new KinopoiskAwardPersonInfo
                        {
                            KinopoiskId = person.KinopoiskId,
                            Name = person.NameRu ?? string.Empty,
                            OriginalName = person.NameEn ?? string.Empty,
                            Profession = person.Profession ?? string.Empty
                        })
                        .ToArray()
                })
                .OrderByDescending(item => item.Win)
                .ThenByDescending(item => item.Year)
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            return new KinopoiskAwardsResponse
            {
                Total = source.Total > 0 ? source.Total : items.Length,
                Items = items
            };
        }

        private sealed class FactsWireResponse
        {
            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("items")]
            public ICollection<FactWireItem> Items { get; set; } = Array.Empty<FactWireItem>();
        }

        private sealed class FactWireItem
        {
            [JsonProperty("text")]
            public string? Text { get; set; }

            [JsonProperty("type")]
            public string? Type { get; set; }

            [JsonProperty("spoiler")]
            public bool Spoiler { get; set; }
        }

        private sealed class BoxOfficeWireResponse
        {
            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("items")]
            public ICollection<BoxOfficeWireItem> Items { get; set; }
                = Array.Empty<BoxOfficeWireItem>();
        }

        private sealed class BoxOfficeWireItem
        {
            [JsonProperty("type")]
            public string? Type { get; set; }

            [JsonProperty("amount")]
            public decimal Amount { get; set; }

            [JsonProperty("currency")]
            public string? Currency { get; set; }

            [JsonProperty("symbol")]
            public string? Symbol { get; set; }
        }

        private sealed class AwardsWireResponse
        {
            [JsonProperty("total")]
            public int Total { get; set; }

            [JsonProperty("items")]
            public ICollection<AwardWireItem> Items { get; set; } = Array.Empty<AwardWireItem>();
        }

        private sealed class AwardWireItem
        {
            [JsonProperty("name")]
            public string? Name { get; set; }

            [JsonProperty("nominationName")]
            public string? NominationName { get; set; }

            [JsonProperty("year")]
            public int Year { get; set; }

            [JsonProperty("win")]
            public bool Win { get; set; }

            [JsonProperty("imageUrl")]
            public string? ImageUrl { get; set; }

            [JsonProperty("persons")]
            public ICollection<AwardPersonWireItem>? Persons { get; set; }
                = Array.Empty<AwardPersonWireItem>();
        }

        private sealed class AwardPersonWireItem
        {
            [JsonProperty("kinopoiskId")]
            public int KinopoiskId { get; set; }

            [JsonProperty("nameRu")]
            public string? NameRu { get; set; }

            [JsonProperty("nameEn")]
            public string? NameEn { get; set; }

            [JsonProperty("profession")]
            public string? Profession { get; set; }
        }
    }
}
