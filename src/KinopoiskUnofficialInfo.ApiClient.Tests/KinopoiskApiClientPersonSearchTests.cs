using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientPersonSearchTests
    {
        private const string SearchJson = "{\"total\":1,\"items\":[{\"kinopoiskId\":66539,\"webUrl\":\"10096\",\"nameRu\":\"Винс Гиллиган\",\"nameEn\":\"Vince Gilligan\",\"sex\":\"MALE\",\"posterUrl\":\"https://example.org/person.jpg\"}]}";

        [Fact]
        public async Task ShouldRequestEncodedNameAndSelectedPage()
        {
            var handler = new RecordingHandler(SearchJson);
            var client = CreateClient(handler);

            var response = await client.SearchPersons(
                "  Винс Гиллиган  ",
                2,
                CancellationToken.None);

            Assert.Equal(1, response.Total);
            var person = Assert.Single(response.Items);
            Assert.Equal(66539, person.KinopoiskId);
            Assert.Equal("Винс Гиллиган", person.NameRu);
            Assert.Equal("Vince Gilligan", person.NameEn);
            Assert.Equal("https://example.org/person.jpg", person.PosterUrl);
            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v1/persons?name=%D0%92%D0%B8%D0%BD%D1%81%20%D0%93%D0%B8%D0%BB%D0%BB%D0%B8%D0%B3%D0%B0%D0%BD&page=2",
                handler.RequestUri?.AbsoluteUri);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(3)]
        public async Task ShouldNormalizeUnsupportedPageToFirst(int page)
        {
            var handler = new RecordingHandler(SearchJson);
            var client = CreateClient(handler);

            await client.SearchPersons("Vince Gilligan", page, CancellationToken.None);

            Assert.EndsWith("&page=1", handler.RequestUri?.AbsoluteUri, StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShouldRejectEmptyPersonName()
        {
            var client = CreateClient(new RecordingHandler(SearchJson));

            await Assert.ThrowsAsync<ArgumentException>(() =>
                client.SearchPersons("   ", 1, CancellationToken.None));
        }

        private static KinopoiskApiClient CreateClient(HttpMessageHandler handler)
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
            private readonly string _content;

            public RecordingHandler(string content)
            {
                _content = content;
            }

            public Uri RequestUri { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestUri = request.RequestUri;
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_content)
                });
            }
        }
    }
}
