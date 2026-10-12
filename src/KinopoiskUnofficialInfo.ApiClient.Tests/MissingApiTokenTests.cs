using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class MissingApiTokenTests
    {
        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ShouldCreateClientsWithoutTokenAndRejectRequestsBeforeSending(string token)
        {
            var factory = new RecordingFactory();
            var client = new KinopoiskApiClient(token, NullLogger<KinopoiskApiClient>.Instance, factory);
            var relations = new KinopoiskRelationsApiClient(token, factory, NullLogger<KinopoiskRelationsApiClient>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetSingleFilm(1));
            await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetApiQuota(CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => relations.GetRelations(1));

            Assert.Equal(0, factory.SentRequests);
            Assert.False(factory.Client.DefaultRequestHeaders.Contains("X-API-KEY"));
        }

        [Fact]
        public async Task ShouldPreserveCancellationBeforeMissingTokenFailure()
        {
            var factory = new RecordingFactory();
            var client = new KinopoiskApiClient("", NullLogger<KinopoiskApiClient>.Instance, factory);
            var relations = new KinopoiskRelationsApiClient("", factory, NullLogger<KinopoiskRelationsApiClient>.Instance);
            var token = new CancellationToken(canceled: true);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.GetSingleFilm(1, token));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => relations.GetRelations(1, token));
            Assert.Equal(0, factory.SentRequests);
        }

        private sealed class RecordingFactory : HttpMessageHandler, IHttpClientFactory
        {
            public int SentRequests { get; private set; }
            public HttpClient Client { get; }

            public RecordingFactory() => Client = new HttpClient(this);

            public HttpClient CreateClient(string name) => Client;

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                SentRequests++;
                throw new InvalidOperationException("Запрос без настроенного токена не должен отправляться.");
            }
        }
    }
}
