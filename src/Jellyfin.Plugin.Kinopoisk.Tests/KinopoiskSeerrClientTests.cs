using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Caching.Memory;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests;

public sealed class KinopoiskSeerrClientTests
{
    private static readonly Guid First = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    [Theory]
    [InlineData("disabled", "disabled")]
    [InlineData("enhanced", "enhanced")]
    [InlineData("auto", "enhanced")]
    public async Task OptionalConnectorsDoNotContactSeerr(string mode, string provider)
    {
        using var fixture = new Fixture();
        fixture.Config = new PluginConfiguration { RecommendationRequestProvider = mode };
        var status = await fixture.Client.Status(First, CancellationToken.None);
        Assert.Equal(provider, status.GetProperty("provider").GetString());
        Assert.Empty(fixture.Http.Calls);
    }

    [Fact]
    public async Task UsesLinkedIdentityAndAppliesSavedSettingsImmediately()
    {
        using var fixture = new Fixture();
        var status = await fixture.Client.Status(First, CancellationToken.None);
        Assert.True(status.GetProperty("canRequestMovie").GetBoolean());
        await fixture.Client.Get(Second, "movie/501", CancellationToken.None);
        fixture.Config = new PluginConfiguration { RecommendationRequestProvider = "seerr", SeerrUrl = "http://new-seerr:5055/base", SeerrApiKey = "new-key" };
        await fixture.Client.Get(First, "tv/902", CancellationToken.None);
        var reads = fixture.Http.Calls.Where(c => !c.Path.Contains("/user?")).ToArray();
        Assert.Equal(new[] { "17", "28", "17" }, reads.Select(c => c.User));
        Assert.EndsWith("/base/api/v1/tv/902", reads[2].Path);
        Assert.Equal("new-key", reads[2].Key);
        Assert.DoesNotContain("fixture-service-key", status.GetRawText());
    }

    [Fact]
    public async Task RefreshesLinkBeforePostAndNeverUsesAdministratorFallback()
    {
        using var fixture = new Fixture();
        await fixture.Client.Status(First, CancellationToken.None);
        fixture.Http.Linked = false;
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Request(First,
            JsonSerializer.SerializeToElement(new { mediaId = 501, mediaType = "movie" }), CancellationToken.None));
        Assert.Equal(403, error.Status);
        Assert.DoesNotContain(fixture.Http.Calls, c => c.Method == "POST");
    }

    [Fact]
    public async Task RequestUsesUsersIdentityAndDoesNotRetryUpstreamError()
    {
        using var fixture = new Fixture();
        fixture.Http.PostStatus = HttpStatusCode.TooManyRequests;
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Request(Second,
            JsonSerializer.SerializeToElement(new { mediaId = 501, mediaType = "tv", seasons = new[] { 1, 3 } }), CancellationToken.None));
        Assert.Equal(429, error.Status);
        var call = Assert.Single(fixture.Http.Calls.Where(c => c.Method == "POST"));
        Assert.Equal("28", call.User);
        Assert.Equal("{\"mediaId\":501,\"mediaType\":\"tv\",\"seasons\":[1,3]}", call.Body);
    }

    [Theory]
    [InlineData("{\"mediaId\":501,\"mediaType\":\"movie\",\"requestedBy\":1}")]
    [InlineData("{\"mediaId\":501,\"mediaType\":\"tv\",\"seasons\":[]}")]
    [InlineData("{\"mediaId\":501,\"mediaType\":\"tv\",\"seasons\":[\"all\"]}")]
    [InlineData("{\"mediaId\":\"501\",\"mediaType\":\"movie\"}")]
    [InlineData("{\"mediaId\":501,\"mediaType\":\"movie\",\"serverId\":99}")]
    public async Task RejectsImpersonationAndInvalidRequestBeforeNetwork(string json)
    {
        using var fixture = new Fixture();
        using var body = JsonDocument.Parse(json);
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Request(First, body.RootElement, CancellationToken.None));
        Assert.Equal(400, error.Status);
        Assert.Empty(fixture.Http.Calls);
    }

    [Fact]
    public async Task FollowsPaginationAndRejectsAmbiguousLinks()
    {
        using var fixture = new Fixture();
        fixture.Http.Paginate = true;
        await fixture.Client.Get(First, "movie/501", CancellationToken.None);
        Assert.Contains(fixture.Http.Calls, c => c.Path.Contains("skip=100"));
        fixture.Http.Duplicate = true;
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Request(First,
            JsonSerializer.SerializeToElement(new { mediaId = 501, mediaType = "movie" }), CancellationToken.None));
        Assert.Equal(409, error.Status);
        Assert.DoesNotContain(fixture.Http.Calls, c => c.Method == "POST");
    }

    private sealed class Fixture : IDisposable
    {
        public PluginConfiguration Config = new() { RecommendationRequestProvider = "seerr", SeerrUrl = "http://seerr:5055", SeerrApiKey = "fixture-service-key" };
        public readonly Transport Http = new();
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        public KinopoiskSeerrClient Client { get; }
        public Fixture()
        {
            var factory = new Mock<IHttpClientFactory>();
            factory.Setup(f => f.CreateClient(KinopoiskSeerrClient.HttpClientName)).Returns(() => new HttpClient(Http, false));
            Client = new KinopoiskSeerrClient(factory.Object, _cache, () => Config);
        }
        public void Dispose() { _cache.Dispose(); Http.Dispose(); }
    }

    private sealed class Transport : HttpMessageHandler
    {
        public readonly List<(string Method, string Path, string Key, string User, string Body)> Calls = new();
        public bool Linked = true;
        public bool Paginate;
        public bool Duplicate;
        public HttpStatusCode PostStatus = HttpStatusCode.Created;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Calls.Add((request.Method.Method, uri.AbsoluteUri, request.Headers.GetValues("X-Api-Key").Single(),
                request.Headers.TryGetValues("X-Api-User", out var values) ? values.Single() : "",
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(token)));
            object body = new { id = 501 };
            if (uri.AbsolutePath.EndsWith("/user"))
            {
                var users = new List<object>();
                if (Paginate && uri.Query.Contains("skip=0"))
                    users.AddRange(Enumerable.Range(100, 100).Select(id => (object)new { id, jellyfinUserId = Guid.NewGuid().ToString() }));
                else if (Linked)
                {
                    users.Add(new { id = 17, jellyfinUserId = First.ToString("N") });
                    users.Add(new { id = 28, jellyfinUserId = Second.ToString() });
                    if (Duplicate) users.Add(new { id = 29, jellyfinUserId = First.ToString() });
                }
                body = new { results = users };
            }
            if (uri.AbsolutePath.EndsWith("/auth/me")) body = new { permissions = 32 };
            return new HttpResponseMessage(request.Method == HttpMethod.Post ? PostStatus : HttpStatusCode.OK)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
        }
    }
}
