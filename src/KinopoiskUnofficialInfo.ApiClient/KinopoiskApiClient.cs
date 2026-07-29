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
    public class KinopoiskApiClient : IFilteredKinopoiskApiClient
    {
        private const string ApiBaseUrl = "https://kinopoiskapiunofficial.tech";

        private readonly string _apiToken;
        private readonly ILogger<KinopoiskApiClient> _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly HttpClient _httpClient;
        private readonly Client _apiClient;

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
            try
            {
                _logger.LogDebug("{MemberName} request starting...", memberName);
                var res = await method.Invoke(ct ?? CancellationToken.None);
                _logger.LogDebug("{MemberName} request complete successfully", memberName);
                return res;
            }
            catch (ApiException e)
            {
                _logger.LogError(
                    "Received non-success result status code {StatusCode} from Kinopoisk API, response content is:\n{Response}",
                    e.StatusCode,
                    e.Response);
                throw;
            }
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

        public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
        {
            return Invoke(async (ct) => {
                try {
                    return await _apiClient.VideosAsync(filmId, ct);
                } catch (ApiException e)
                {
                    if (e.StatusCode == 404)
                        return new VideoResponse();
                    throw;
                }
            }, cancellationToken);
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
            using var request = new HttpRequestMessage(HttpMethod.Get, requestUri);
            request.Headers.Accept.ParseAdd("application/json");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

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

            return JsonConvert.DeserializeObject<FilteredFilmSearchResponse>(responseText)
                ?? new FilteredFilmSearchResponse();
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
