using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class ApiTokenReloadTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(1)]
        [InlineData(2)]
        [InlineData(3)]
        public async Task ShouldApplyAddedReplacedAndRemovedTokenOnExistingClient(int endpoint)
        {
            var token = "";
            using var factory = new RecordingFactory();
            var client = new KinopoiskApiClient("", NullLogger<KinopoiskApiClient>.Instance,
                factory, () => token);
            var relations = new KinopoiskRelationsApiClient("", factory,
                NullLogger<KinopoiskRelationsApiClient>.Instance, () => token);
            Func<Task> request = endpoint switch
            {
                0 => () => client.GetStaff(1),
                1 => () => client.GetImages(1, FilmImageType.STILL, 1, CancellationToken.None),
                2 => () => client.GetApiQuota(CancellationToken.None),
                _ => () => relations.GetRelations(1)
            };

            await Assert.ThrowsAsync<InvalidOperationException>(request);
            Assert.Empty(factory.Requests);
            token = " first-key ";
            await request();
            token = "second-key";
            await request();
            token = " ";
            await Assert.ThrowsAsync<InvalidOperationException>(request);

            Assert.Equal(new[] { "first-key", "second-key" }, factory.Requests.Select(r => r.Token));
            Assert.False(factory.Client.DefaultRequestHeaders.Contains("X-API-KEY"));
            if (endpoint == 2)
                Assert.All(factory.Requests, r => Assert.EndsWith("/" + r.Token, r.Url));
        }

        [Fact]
        public async Task ShouldRecoverFromUnauthorizedResponseAfterTokenReplacement()
        {
            var token = "expired-key";
            using var factory = new RecordingFactory();
            factory.Status = HttpStatusCode.Unauthorized;
            var client = new KinopoiskApiClient("", NullLogger<KinopoiskApiClient>.Instance,
                factory, () => token);

            var failure = await Assert.ThrowsAsync<ApiException>(() => client.GetStaff(1));
            Assert.Equal(401, failure.StatusCode);
            token = "replacement-key";
            factory.Status = HttpStatusCode.OK;
            await client.GetStaff(1);

            Assert.Equal(new[] { "expired-key", "replacement-key" }, factory.Requests.Select(r => r.Token));
        }

        [Fact]
        public async Task ShouldUseOneTokenSnapshotForQuotaPathAndHeader()
        {
            var reads = 0;
            using var factory = new RecordingFactory();
            var client = new KinopoiskApiClient("", NullLogger<KinopoiskApiClient>.Instance,
                factory, () => "key-" + Interlocked.Increment(ref reads));

            await client.GetApiQuota(CancellationToken.None);

            var request = Assert.Single(factory.Requests);
            Assert.EndsWith("/" + request.Token, request.Url);
        }

        [Fact]
        public async Task ShouldPreserveInFlightHeaderWhenAnotherClientUsesNewToken()
        {
            var token = "first-key";
            using var factory = new RecordingFactory();
            var started = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            factory.BeforeRecord = async request =>
            {
                if (request.RequestUri!.AbsolutePath == "/api/v1/staff")
                {
                    started.TrySetResult(true);
                    await release.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            };
            var client = new KinopoiskApiClient("", NullLogger<KinopoiskApiClient>.Instance,
                factory, () => Volatile.Read(ref token));
            var relations = new KinopoiskRelationsApiClient("", factory,
                NullLogger<KinopoiskRelationsApiClient>.Instance, () => Volatile.Read(ref token));

            var pending = client.GetStaff(1);
            try
            {
                await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
                Volatile.Write(ref token, "second-key");
                await relations.GetRelations(1);
            }
            finally
            {
                release.TrySetResult(true);
            }
            await pending;

            Assert.Contains(factory.Requests, r => r.Url.Contains("/staff") && r.Token == "first-key");
            Assert.Contains(factory.Requests, r => r.Url.EndsWith("/relations") && r.Token == "second-key");
            Assert.False(factory.Client.DefaultRequestHeaders.Contains("X-API-KEY"));
        }

        private sealed class RecordingFactory : HttpMessageHandler, IHttpClientFactory
        {
            public ConcurrentQueue<(string Url, string Token)> Requests { get; } = new();
            public HttpStatusCode Status { get; set; } = HttpStatusCode.OK;
            public Func<HttpRequestMessage, Task> BeforeRecord { get; set; }
            public HttpClient Client { get; }
            public RecordingFactory() => Client = new HttpClient(this, disposeHandler: false);
            public HttpClient CreateClient(string name) => Client;

            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                if (BeforeRecord is not null)
                    await BeforeRecord(request);
                Requests.Enqueue((request.RequestUri!.AbsoluteUri, request.Headers.GetValues("X-API-KEY").Single()));
                var json = request.RequestUri.AbsolutePath == "/api/v1/staff"
                    ? "[]" : "{\"total\":0,\"totalPages\":0,\"items\":[],\"accountType\":\"FREE\"}";
                return new HttpResponseMessage(Status) { Content = new StringContent(json) };
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) Client.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
