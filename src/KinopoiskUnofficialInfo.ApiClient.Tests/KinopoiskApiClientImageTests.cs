using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientImageTests
    {
        private const string ImageJson = "{\"total\":1,\"totalPages\":2,\"items\":[{\"imageUrl\":\"https://example.org/original.jpg\",\"previewUrl\":\"https://example.org/preview.jpg\"}]}";

        [Fact]
        public async Task ShouldRequestSelectedImageTypeAndPage()
        {
            var handler = new RecordingHandler(ImageJson);
            var client = CreateClient(handler);

            var response = await client.GetImages(
                5273,
                FilmImageType.FAN_ART,
                3,
                CancellationToken.None);

            Assert.Equal(1, response.Total);
            Assert.Equal(2, response.TotalPages);
            var image = Assert.Single(response.Items);
            Assert.Equal("https://example.org/original.jpg", image.ImageUrl);
            Assert.Equal("https://example.org/preview.jpg", image.PreviewUrl);
            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v2.2/films/5273/images?type=FAN_ART&page=3",
                handler.RequestUri?.AbsoluteUri);
        }

        [Theory]
        [InlineData(0)]
        [InlineData(21)]
        public async Task ShouldNormalizeUnsupportedPageToFirst(int page)
        {
            var handler = new RecordingHandler(ImageJson);
            var client = CreateClient(handler);

            await client.GetImages(430, FilmImageType.POSTER, page, CancellationToken.None);

            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v2.2/films/430/images?type=POSTER&page=1",
                handler.RequestUri?.AbsoluteUri);
        }

        [Fact]
        public async Task ShouldRejectNonPositiveFilmIdentifier()
        {
            var client = CreateClient(new RecordingHandler(ImageJson));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
                client.GetImages(0, FilmImageType.STILL, 1, CancellationToken.None));
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
