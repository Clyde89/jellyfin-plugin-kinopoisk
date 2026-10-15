using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Presentation;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Model.Serialization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    [Collection("Запуск без токена")]
    public class ApiTokenConfigurationReloadTests
    {
        [Fact]
        public async Task ShouldApplySavedTokenAcrossAlreadyResolvedServices()
        {
            using var fixture = new Fixture();
            var api = fixture.Services.GetRequiredService<KinopoiskApiClient>();
            var relations = fixture.Services.GetRequiredService<IKinopoiskRelationsApiClient>();
            var supplemental = fixture.Services.GetRequiredService<KinopoiskSupplementalApiClient>();
            var similar = fixture.Services.GetRequiredService<KinopoiskSimilarApiClient>();
            async Task RequestAll(int id)
            {
                await api.GetStaff(id);
                await relations.GetRelations(id);
                await supplemental.GetFacts(id, CancellationToken.None);
                await similar.GetSimilar(id, CancellationToken.None);
            }

            await Assert.ThrowsAsync<InvalidOperationException>(() => RequestAll(1));
            fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = " first-key " });
            await RequestAll(1);
            fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = "second-key" });
            await RequestAll(2);
            fixture.Plugin.UpdateConfiguration(new PluginConfiguration());

            await Assert.ThrowsAsync<InvalidOperationException>(() => api.GetStaff(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => relations.GetRelations(3));
            await Assert.ThrowsAsync<InvalidOperationException>(() => supplemental.GetFacts(3, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => similar.GetSimilar(3, CancellationToken.None));
            Assert.Equal(Enumerable.Repeat("first-key", 4).Concat(Enumerable.Repeat("second-key", 4)),
                fixture.Http.Tokens);
        }

        [Fact]
        public async Task ShouldRefreshQuotaImmediatelyWhenSavedTokenChanged()
        {
            using var fixture = new Fixture();
            using var monitor = new KinopoiskQuotaMonitor(fixture.Services, fixture.Diagnostics,
                NullLogger<KinopoiskQuotaMonitor>.Instance);
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = "first-key" });
                await WaitUntil(() => fixture.Diagnostics.GetSnapshot().LastQuotaCheckUtc.HasValue);
                Assert.Equal("FREE", fixture.Diagnostics.GetSnapshot().AccountType);
                fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = "second-key" });
                await WaitUntil(() => fixture.Http.Tokens.Count == 2
                    && fixture.Diagnostics.GetSnapshot().LastQuotaCheckUtc.HasValue);
                fixture.Plugin.UpdateConfiguration(new PluginConfiguration());

                Assert.Null(fixture.Diagnostics.GetSnapshot().LastQuotaCheckUtc);
                Assert.Equal(string.Empty, fixture.Diagnostics.GetSnapshot().AccountType);
                Assert.Equal(new[] { "first-key", "second-key" }, fixture.Http.Tokens);
            }
            finally
            {
                await monitor.StopAsync(CancellationToken.None);
            }
            Assert.Null(fixture.Plugin.ConfigurationChanged);
        }

        private static async Task WaitUntil(Func<bool> condition)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            while (!condition())
                await Task.Delay(10, timeout.Token);
        }

        [Fact]
        public async Task ShouldDiscardQuotaResponseForReplacedToken()
        {
            using var fixture = new Fixture();
            var firstStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var secondStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseFirst = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var releaseSecond = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            fixture.Http.BeforeResponse = async request =>
            {
                if (request.Headers.GetValues("X-API-KEY").Single() == "first-key")
                {
                    firstStarted.TrySetResult(true);
                    await releaseFirst.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
                else
                {
                    secondStarted.TrySetResult(true);
                    await releaseSecond.Task.WaitAsync(TimeSpan.FromSeconds(5));
                }
            };
            using var monitor = new KinopoiskQuotaMonitor(fixture.Services, fixture.Diagnostics,
                NullLogger<KinopoiskQuotaMonitor>.Instance);
            await monitor.StartAsync(CancellationToken.None);
            try
            {
                fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = "first-key" });
                await firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                fixture.Plugin.UpdateConfiguration(new PluginConfiguration { ApiToken = "second-key" });
                releaseFirst.TrySetResult(true);
                await secondStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

                Assert.Null(fixture.Diagnostics.GetSnapshot().LastQuotaCheckUtc);
                releaseSecond.TrySetResult(true);
                await WaitUntil(() => fixture.Diagnostics.GetSnapshot().LastQuotaCheckUtc.HasValue);
                Assert.Equal(new[] { "first-key", "second-key" }, fixture.Http.Tokens);
            }
            finally
            {
                releaseFirst.TrySetResult(true);
                releaseSecond.TrySetResult(true);
                await monitor.StopAsync(CancellationToken.None);
            }
        }

        private sealed class Fixture : IDisposable
        {
            private readonly FieldInfo _instance = typeof(Plugin).GetField("<Instance>k__BackingField",
                BindingFlags.Static | BindingFlags.NonPublic)!;
            private readonly object _previous;
            private readonly string _root = Path.Combine(Path.GetTempPath(), "kinopoisk-reload-" + Guid.NewGuid().ToString("N"));
            public Plugin Plugin { get; }
            public ServiceProvider Services { get; }
            public RecordingFactory Http { get; } = new();
            public KinopoiskDiagnostics Diagnostics { get; } = new();

            public Fixture()
            {
                _previous = _instance.GetValue(null);
                Directory.CreateDirectory(_root);
                var paths = new Mock<IApplicationPaths>();
                paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(_root, "plugins"));
                paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(_root, "configurations"));
                var xml = new Mock<IXmlSerializer>();
                xml.Setup(x => x.DeserializeFromFile(It.IsAny<System.Type>(), It.IsAny<string>()))
                    .Returns(new PluginConfiguration());
                Plugin = new Plugin(paths.Object, xml.Object);
                var services = new ServiceCollection();
                services.AddLogging();
                services.AddMemoryCache();
                services.AddSingleton<IHttpClientFactory>(Http);
                new KinopoiskPluginServiceRegistrator().RegisterServices(services, null!);
                services.AddSingleton(Diagnostics);
                Services = services.BuildServiceProvider();
            }

            public void Dispose()
            {
                Services.Dispose();
                Http.Dispose();
                _instance.SetValue(null, _previous);
                Directory.Delete(_root, recursive: true);
            }
        }

        private sealed class RecordingFactory : HttpMessageHandler, IHttpClientFactory
        {
            public ConcurrentQueue<string> Tokens { get; } = new();
            public Func<HttpRequestMessage, Task> BeforeResponse { get; set; }
            public HttpClient CreateClient(string name) => new HttpClient(this, disposeHandler: false);
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            {
                Tokens.Enqueue(request.Headers.GetValues("X-API-KEY").Single());
                if (BeforeResponse is not null)
                    await BeforeResponse(request);
                var json = request.RequestUri!.AbsolutePath == "/api/v1/staff"
                    ? "[]" : "{\"total\":0,\"items\":[],\"accountType\":\"FREE\"}";
                return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) };
            }
        }
    }
}
