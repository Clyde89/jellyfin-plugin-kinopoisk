using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Api;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using System.Text.Encodings.Web;
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
    public async Task ConnectionCheckUsesDraftWithoutChangingSavedSettingsOrCreatingRequests()
    {
        using var fixture = new Fixture();
        var saved = fixture.Config;
        var status = await fixture.Client.CheckConnection(First, "seerr", " http://draft-seerr:5055/base/ ", " draft-key ", CancellationToken.None);
        Assert.Same(saved, fixture.Config);
        Assert.Equal("http://seerr:5055", saved.SeerrUrl);
        Assert.True(status.GetProperty("userFound").GetBoolean());
        Assert.All(fixture.Http.Calls, call =>
        {
            Assert.StartsWith("http://draft-seerr:5055/base/api/v1/", call.Path);
            Assert.Equal("draft-key", call.Key);
            Assert.Equal("GET", call.Method);
        });
        Assert.DoesNotContain("draft-key", status.GetRawText());
        await fixture.Client.Get(First, "movie/501", CancellationToken.None);
        Assert.StartsWith("http://seerr:5055/api/v1/", fixture.Http.Calls.Last().Path);
        Assert.Equal("fixture-service-key", fixture.Http.Calls.Last().Key);
    }

    [Fact]
    public async Task ConnectionCheckDoesNotStoreDraftCredentialsInUserMappingCache()
    {
        using var fixture = new Fixture();
        await fixture.Client.CheckConnection(First, "seerr", "http://draft-seerr:5055", "draft-key", CancellationToken.None);
        fixture.Config = new PluginConfiguration { RecommendationRequestProvider = "seerr", SeerrUrl = "http://draft-seerr:5055", SeerrApiKey = "draft-key" };
        fixture.Http.Linked = false;
        var status = await fixture.Client.Status(First, CancellationToken.None);
        Assert.False(status.GetProperty("userFound").GetBoolean());
    }

    [Theory]
    [InlineData("invalid", "http://seerr:5055", "key", 400)]
    [InlineData("seerr", "file:///etc/config", "key", 409)]
    [InlineData("seerr", "http://seerr:5055?key=test", "key", 409)]
    [InlineData("seerr", "http://seerr:5055", "", 409)]
    [InlineData("seerr", "http://seerr:5055", "bad\nkey", 409)]
    public async Task InvalidDraftIsRejectedBeforeNetwork(string provider, string url, string key, int expectedStatus)
    {
        using var fixture = new Fixture();
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.CheckConnection(First, provider, url, key, CancellationToken.None));
        Assert.Equal(expectedStatus, error.Status);
        Assert.Empty(fixture.Http.Calls);
    }

    [Fact]
    public async Task SeerrKeyRejectionIsNotReportedAsExpiredJellyfinSession()
    {
        using var fixture = new Fixture();
        fixture.Http.ReadStatus = HttpStatusCode.Unauthorized;
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.CheckConnection(First,
            "seerr", "http://seerr:5055", "invalid-key", CancellationToken.None));
        Assert.Equal(502, error.Status);
        Assert.Contains("Seerr отклонил API-ключ", error.Message);
        Assert.DoesNotContain("invalid-key", error.Message);
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

    [Fact]
    public async Task ControllerIgnoresUserHeadersAndUsesAuthenticatedPrincipal()
    {
        using var fixture = new Fixture();
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Emby-UserId"] = First.ToString();
        context.Request.Headers["X-Api-User"] = "1";
        var controller = new KinopoiskRequestsController(fixture.Client)
        {
            ControllerContext = new ControllerContext { HttpContext = context }
        };
        Assert.IsType<ForbidResult>(await controller.Status(CancellationToken.None));
        Assert.Empty(fixture.Http.Calls);
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", Second.ToString()) }, "fixture"));
        Assert.IsType<OkObjectResult>(await controller.Status(CancellationToken.None));
        Assert.Equal("28", fixture.Http.Calls.Last().User);
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task RoutesActivateInMvcAndRejectArbitraryProxyPaths()
    {
        using var fixture = new Fixture();
        var builder = WebApplication.CreateBuilder();
        var administrator = false;
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(fixture.Client);
        builder.Services.AddAuthentication("fixture").AddScheme<AuthenticationSchemeOptions, FixtureAuthenticationHandler>("fixture", _ => { });
        builder.Services.AddAuthorization(options => options.AddPolicy(Policies.RequiresElevation,
            policy => policy.RequireClaim("fixture-admin", "true")));
        builder.Services.AddControllers().AddApplicationPart(typeof(KinopoiskRequestsController).Assembly);
        await using var app = builder.Build();
        app.Use(async (context, next) =>
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", Second.ToString()),
                new Claim("fixture-admin", administrator ? "true" : "false") }, "fixture"));
            await next(context);
        });
        app.UseAuthorization();
        app.MapControllers();
        await app.StartAsync();
        try
        {
            using var http = new HttpClient { BaseAddress = new Uri(app.Urls.Single()) };
            var draft = "{\"provider\":\"seerr\",\"url\":\"http://draft-seerr:5055\",\"apiKey\":\"draft-key\"}";
            using var denied = new StringContent(draft, Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.Forbidden, (await http.PostAsync("/KinopoiskRequests/connection-test", denied)).StatusCode);
            Assert.Empty(fixture.Http.Calls);
            administrator = true;
            using var preview = new StringContent(draft, Encoding.UTF8, "application/json");
            var previewResult = await http.PostAsync("/KinopoiskRequests/connection-test", preview);
            Assert.Equal(HttpStatusCode.OK, previewResult.StatusCode);
            Assert.DoesNotContain("draft-key", await previewResult.Content.ReadAsStringAsync());
            Assert.Equal("http://seerr:5055", fixture.Config.SeerrUrl);
            Assert.All(fixture.Http.Calls, call => Assert.Equal("GET", call.Method));
            var callsBeforeInvalid = fixture.Http.Calls.Count;
            using var arbitrary = new StringContent(draft.Replace("}", ",\"requestedBy\":1}"), Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/KinopoiskRequests/connection-test", arbitrary)).StatusCode);
            Assert.Equal(callsBeforeInvalid, fixture.Http.Calls.Count);
            administrator = false;
            var status = await http.GetAsync("/KinopoiskRequests/user-status");
            Assert.Equal(HttpStatusCode.OK, status.StatusCode);
            Assert.Contains("\"provider\":\"seerr\"", await status.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/KinopoiskRequests/movie/501")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/KinopoiskRequests/tv/501/recommendations")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/KinopoiskRequests/quota")).StatusCode);
            Assert.Equal("28", fixture.Http.Calls.Last().User);
            Assert.EndsWith("/user/28/quota", fixture.Http.Calls.Last().Path);
            Assert.Equal(HttpStatusCode.OK, (await http.GetAsync("/KinopoiskRequests/link")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/KinopoiskRequests/settings/main")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, (await http.GetAsync("/KinopoiskRequests/user/17/quota")).StatusCode);
            using var forbiddenBody = new StringContent("{\"mediaId\":501,\"mediaType\":\"movie\",\"requestedBy\":1}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.BadRequest, (await http.PostAsync("/KinopoiskRequests/request", forbiddenBody)).StatusCode);
            Assert.DoesNotContain(fixture.Http.Calls, c => c.Method == "POST");
            using var body = new StringContent("{\"mediaId\":501,\"mediaType\":\"movie\"}", Encoding.UTF8, "application/json");
            Assert.Equal(HttpStatusCode.OK, (await http.PostAsync("/KinopoiskRequests/request", body)).StatusCode);
            Assert.Equal("28", Assert.Single(fixture.Http.Calls.Where(c => c.Method == "POST")).User);
        }
        finally { await app.StopAsync(); }
    }

    [Fact]
    public async Task QuotaUsesLinkedUsersIdentityAndCannotSelectAnotherUser()
    {
        using var fixture = new Fixture();
        var quota = await fixture.Client.Quota(Second, CancellationToken.None);
        Assert.Equal(2, quota.GetProperty("tv").GetProperty("remaining").GetInt32());
        var call = fixture.Http.Calls.Last();
        Assert.EndsWith("/user/28/quota", call.Path);
        Assert.Equal("28", call.User);
        fixture.Http.Linked = false;
        await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Quota(First, CancellationToken.None));
        Assert.Single(fixture.Http.Calls.Where(c => c.Path.EndsWith("/quota")));
    }

    [Theory]
    [InlineData("auto", "https://public.example/seerr/", "https://public.example/seerr", true)]
    [InlineData("auto", "", "http://seerr:5055", true)]
    [InlineData("auto", "javascript:alert(1)", "http://seerr:5055", true)]
    [InlineData("auto", "https://name:secret@public.example/", "http://seerr:5055", true)]
    [InlineData("auto", "https://public.example/?key=secret", "http://seerr:5055", true)]
    [InlineData("local", "https://public.example/seerr", "http://seerr:5055", false)]
    [InlineData("custom", "https://public.example/seerr", "https://chosen.example/subpath", false)]
    public async Task ChoosesOnlyValidatedBrowserAddressWithoutExposingSettings(string mode, string applicationUrl, string expected, bool publicRead)
    {
        using var fixture = new Fixture();
        fixture.Config.SeerrLinkMode = mode;
        fixture.Config.SeerrWebUrl = " https://chosen.example/subpath/ ";
        fixture.Http.ApplicationUrl = applicationUrl;
        var result = await fixture.Client.Link(First, CancellationToken.None);
        Assert.Equal(expected, result.GetProperty("url").GetString());
        Assert.Equal(publicRead, fixture.Http.Calls.Any(c => c.Path.EndsWith("/settings/public")));
        Assert.DoesNotContain("private-setting", result.GetRawText());
        Assert.DoesNotContain("fixture-service-key", result.GetRawText());
        Assert.All(fixture.Http.Calls.Where(c => c.Path.EndsWith("/settings/public")), c => Assert.Equal("17", c.User));
    }

    [Fact]
    public async Task MissingPublicSettingsFallsBackToLocalAddress()
    {
        using var fixture = new Fixture();
        fixture.Http.PublicStatus = HttpStatusCode.NotFound;
        var result = await fixture.Client.Link(First, CancellationToken.None);
        Assert.Equal("http://seerr:5055", result.GetProperty("url").GetString());
    }

    [Theory]
    [InlineData("https://user:password@example.com/seerr")]
    [InlineData("https://example.com/seerr?token=secret")]
    [InlineData("https://example.com/seerr#fragment")]
    [InlineData("file:///tmp/seerr")]
    [InlineData("https://example.com/\nseerr")]
    public async Task InvalidCustomAddressDoesNotBecomeClickable(string url)
    {
        using var fixture = new Fixture();
        fixture.Config.SeerrLinkMode = "custom";
        fixture.Config.SeerrWebUrl = url;
        var result = await fixture.Client.Link(First, CancellationToken.None);
        Assert.Equal("", result.GetProperty("url").GetString());
        Assert.Empty(fixture.Http.Calls);
    }

    [Fact]
    public async Task AcceptedResponseWithoutRequestIsNotReportedAsCreated()
    {
        using var fixture = new Fixture();
        fixture.Http.PostStatus = HttpStatusCode.Accepted;
        fixture.Http.NoSeasons = true;
        var error = await Assert.ThrowsAsync<KinopoiskSeerrException>(() => fixture.Client.Request(First,
            JsonSerializer.SerializeToElement(new { mediaId = 501, mediaType = "tv", seasons = new[] { 1 } }), CancellationToken.None));
        Assert.Equal(409, error.Status);
        Assert.Contains("не подтвердил создание", error.Message);
        Assert.Single(fixture.Http.Calls.Where(c => c.Method == "POST"));
    }

    private sealed class FixtureAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
    {
        public FixtureAuthenticationHandler(IOptionsMonitor<AuthenticationSchemeOptions> options,
            ILoggerFactory logger, UrlEncoder encoder) : base(options, logger, encoder) { }

        protected override Task<AuthenticateResult> HandleAuthenticateAsync()
            => Task.FromResult(AuthenticateResult.NoResult());
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
        public string ApplicationUrl = "https://public.example/seerr";
        public HttpStatusCode PublicStatus = HttpStatusCode.OK;
        public bool NoSeasons;
        public bool Linked = true;
        public bool Paginate;
        public bool Duplicate;
        public HttpStatusCode PostStatus = HttpStatusCode.Created;
        public HttpStatusCode ReadStatus = HttpStatusCode.OK;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var uri = request.RequestUri!;
            Calls.Add((request.Method.Method, uri.AbsoluteUri, request.Headers.GetValues("X-Api-Key").Single(),
                request.Headers.TryGetValues("X-Api-User", out var values) ? values.Single() : "",
                request.Content == null ? "" : await request.Content.ReadAsStringAsync(token)));
            object body = new { id = 501 };
            if (request.Method == HttpMethod.Post && NoSeasons) body = new { message = "No seasons available to request" };
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
            if (uri.AbsolutePath.EndsWith("/quota")) body = new {
                movie = new { limit = 0, days = 7, used = 0, restricted = false },
                tv = new { limit = 3, days = 7, used = 1, remaining = 2, restricted = false }
            };
            if (uri.AbsolutePath.EndsWith("/settings/public")) body = new { applicationUrl = ApplicationUrl, apiKey = "private-setting" };
            return new HttpResponseMessage(uri.AbsolutePath.EndsWith("/settings/public") ? PublicStatus : request.Method == HttpMethod.Post ? PostStatus : ReadStatus)
            {
                Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json")
            };
        }
    }
}
