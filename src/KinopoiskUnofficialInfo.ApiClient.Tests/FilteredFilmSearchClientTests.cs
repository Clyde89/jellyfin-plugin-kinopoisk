using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class FilteredFilmSearchClientTests
    {
        [Fact]
        public async Task ShouldSendFilteredParametersAndDeserializeResponse()
        {
            const string responseJson = """
                {
                  "total": 1,
                  "totalPages": 1,
                  "items": [
                    {
                      "kinopoiskId": 5273,
                      "imdbId": "tt0298148",
                      "nameRu": "Шрэк 2",
                      "nameEn": "Shrek 2",
                      "nameOriginal": "Shrek 2",
                      "year": 2004,
                      "type": "FILM",
                      "posterUrl": "https://example.test/poster.jpg",
                      "posterUrlPreview": "https://example.test/poster-preview.jpg"
                    }
                  ]
                }
                """;

            var handler = new RecordingHandler(responseJson);
            var client = CreateClient(handler);

            var result = await client.SearchFilms(
                new FilmSearchQuery
                {
                    ImdbId = "tt0298148",
                    Keyword = "Shrek 2",
                    YearFrom = 2004,
                    YearTo = 2004,
                    Type = "FILM",
                    Page = 2
                },
                CancellationToken.None);

            Assert.NotNull(handler.LastRequestUri);
            Assert.Equal("/api/v2.2/films", handler.LastRequestUri.AbsolutePath);
            Assert.Equal(
                "?imdbId=tt0298148&keyword=Shrek%202&yearFrom=2004&yearTo=2004&type=FILM&page=2",
                handler.LastRequestUri.Query);
            Assert.Equal("test-token", handler.ApiKey);

            Assert.Equal(1, result.Total);
            Assert.Equal(1, result.TotalPages);

            var item = Assert.Single(result.Items);
            Assert.Equal(5273, item.KinopoiskId);
            Assert.Equal("tt0298148", item.ImdbId);
            Assert.Equal("Шрэк 2", item.NameRu);
            Assert.Equal("Shrek 2", item.NameEn);
            Assert.Equal("Shrek 2", item.NameOriginal);
            Assert.Equal(2004, item.Year);
            Assert.Equal("FILM", item.Type);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(-1)]
        [InlineData(21)]
        public async Task ShouldNormalizeInvalidPageNumber(int page)
        {
            var handler = new RecordingHandler(
                "{\"total\":0,\"totalPages\":0,\"items\":[]}");
            var client = CreateClient(handler);

            await client.SearchFilms(
                new FilmSearchQuery
                {
                    Keyword = "Шрэк",
                    Type = "FILM",
                    Page = page
                },
                CancellationToken.None);

            Assert.NotNull(handler.LastRequestUri);
            Assert.Equal("?keyword=%D0%A8%D1%80%D1%8D%D0%BA&type=FILM&page=1", handler.LastRequestUri.Query);
        }

        private static KinopoiskApiClient CreateClient(RecordingHandler handler)
        {
            return new KinopoiskApiClient(
                "test-token",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(handler)));
        }

        private sealed class FixedHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _httpClient;

            public FixedHttpClientFactory(HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public HttpClient CreateClient(string name)
            {
                return _httpClient;
            }
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly string _responseJson;

            public RecordingHandler(string responseJson)
            {
                _responseJson = responseJson;
            }

            public Uri LastRequestUri { get; private set; }

            public string ApiKey { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                LastRequestUri = request.RequestUri;

                if (request.Headers.TryGetValues("X-API-KEY", out var values))
                    ApiKey = string.Join(",", values);

                return Task.FromResult(
                    new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(_responseJson)
                    });
            }
        }
    }
}
