using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientDistributionTests
    {
        private const string DistributionJson = "{\"total\":1,\"items\":[{\"type\":\"WORLD_PREMIER\",\"subType\":\"CINEMA\",\"date\":\"2004-05-19\",\"reRelease\":false,\"country\":{\"country\":\"США\"},\"companies\":[]}]}";

        [Fact]
        public async Task ShouldRequestAndParseDistributions()
        {
            var handler = new RecordingHandler(DistributionJson);
            var client = new KinopoiskApiClient(
                "test-token",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(handler)));

            var result = await client.GetDistributions(5273, CancellationToken.None);

            var distribution = Assert.Single(result.Items);
            Assert.Equal(DistributionType.WORLD_PREMIER, distribution.Type);
            Assert.Equal(DistributionSubType.CINEMA, distribution.SubType);
            Assert.Equal("2004-05-19", distribution.Date);
            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v2.2/films/5273/distributions",
                handler.RequestUri?.AbsoluteUri);
        }

        [Fact]
        public async Task ShouldRejectInvalidFilmId()
        {
            var client = new KinopoiskApiClient(
                "test-token",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(new RecordingHandler(DistributionJson))));

            await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
                () => client.GetDistributions(0, CancellationToken.None));
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
