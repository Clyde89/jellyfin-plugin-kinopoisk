using System;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientQuotaTests
    {
        private const string QuotaJson = "{\"accountType\":\"FREE\",\"totalQuota\":{\"value\":100000,\"used\":321},\"dailyQuota\":{\"value\":500,\"used\":27}}";

        [Fact]
        public async Task ShouldRequestAndParseApiQuota()
        {
            var handler = new RecordingHandler(QuotaJson);
            var client = new KinopoiskApiClient(
                "test-token",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(handler)));

            var quota = await client.GetApiQuota(CancellationToken.None);

            Assert.Equal("FREE", quota.AccountType);
            Assert.Equal(100000, quota.TotalQuota.Value);
            Assert.Equal(321, quota.TotalQuota.Used);
            Assert.Equal(500, quota.DailyQuota.Value);
            Assert.Equal(27, quota.DailyQuota.Used);
            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v1/api_keys/test-token",
                handler.RequestUri?.AbsoluteUri);
        }

        [Fact]
        public async Task ShouldEscapeApiTokenInQuotaRequestPath()
        {
            var handler = new RecordingHandler(QuotaJson);
            var client = new KinopoiskApiClient(
                "test token/segment",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(handler)));

            await client.GetApiQuota(CancellationToken.None);

            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v1/api_keys/test%20token%2Fsegment",
                handler.RequestUri?.AbsoluteUri);
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
