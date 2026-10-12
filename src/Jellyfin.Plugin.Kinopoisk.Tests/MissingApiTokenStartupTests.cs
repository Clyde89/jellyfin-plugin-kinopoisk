using System;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Presentation;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Net;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Serialization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    [CollectionDefinition("Запуск без токена", DisableParallelization = true)]
    public class MissingApiTokenStartupCollection
    {
    }

    [Collection("Запуск без токена")]
    public class MissingApiTokenStartupTests
    {
        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("runtime-test-token")]
        public async Task ShouldBuildNativeTrailerPipelineAndPassHealthRequest(string token)
        {
            var root = Path.Combine(Path.GetTempPath(), "kinopoisk-startup-" + Guid.NewGuid().ToString("N"));
            var instance = typeof(Plugin).GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic)!;
            var previous = instance.GetValue(null);
            Directory.CreateDirectory(root);
            try
            {
                var paths = new Mock<IApplicationPaths>();
                paths.SetupGet(p => p.PluginsPath).Returns(Path.Combine(root, "plugins"));
                paths.SetupGet(p => p.PluginConfigurationsPath).Returns(Path.Combine(root, "configurations"));
                var xml = new Mock<IXmlSerializer>();
                xml.Setup(x => x.DeserializeFromFile(It.IsAny<System.Type>(), It.IsAny<string>()))
                    .Returns(new PluginConfiguration { ApiToken = token });
                _ = new Plugin(paths.Object, xml.Object);

                var services = new ServiceCollection();
                services.AddLogging();
                services.AddMemoryCache();
                services.AddHttpClient();
                services.AddSingleton(paths.Object);
                services.AddSingleton(new Mock<ILibraryManager>().Object);
                services.AddSingleton(new Mock<IItemPersistenceService>().Object);
                services.AddSingleton(new Mock<IMediaEncoder>().Object);
                services.AddSingleton(new Mock<IAuthorizationContext>().Object);
                services.AddSingleton(new Mock<IHostApplicationLifetime>().Object);
                new KinopoiskPluginServiceRegistrator().RegisterServices(services, null!);
                using var provider = services.BuildServiceProvider();
                bool reached = false;
                var middleware = ActivatorUtilities.CreateInstance<KinopoiskNativeTrailerLazyPrepareMiddleware>(
                    provider, (RequestDelegate)(_ => { reached = true; return Task.CompletedTask; }));
                var context = new DefaultHttpContext();
                context.Request.Method = "GET";
                context.Request.Path = "/health";

                await middleware.InvokeAsync(context);

                Assert.True(reached);
                Assert.NotNull(provider.GetRequiredService<KinopoiskSupplementalApiClient>());
                Assert.NotNull(provider.GetRequiredService<KinopoiskSimilarApiClient>());
                Assert.NotNull(provider.GetRequiredService<IKinopoiskRelationsApiClient>());
            }
            finally
            {
                instance.SetValue(null, previous);
                Directory.Delete(root, recursive: true);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        public async Task ShouldRejectUnconfiguredPresentationRequestsWithoutHttp(string token)
        {
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var factory = new RecordingFactory();
            var diagnostics = new KinopoiskDiagnostics();
            var supplemental = new KinopoiskSupplementalApiClient(token, factory, cache, diagnostics,
                NullLogger<KinopoiskSupplementalApiClient>.Instance);
            var similar = new KinopoiskSimilarApiClient(token, factory, new Mock<IKinopoiskApiClient>().Object,
                cache, diagnostics, NullLogger<KinopoiskSimilarApiClient>.Instance);

            await Assert.ThrowsAsync<InvalidOperationException>(() => supplemental.GetFacts(1, CancellationToken.None));
            await Assert.ThrowsAsync<InvalidOperationException>(() => similar.GetSimilar(1, CancellationToken.None));
            Assert.Equal(0, factory.SentRequests);
        }

        private sealed class RecordingFactory : HttpMessageHandler, IHttpClientFactory
        {
            public int SentRequests { get; private set; }
            public HttpClient CreateClient(string name) => new HttpClient(this, disposeHandler: false);
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                SentRequests++;
                throw new InvalidOperationException("Запрос без настроенного токена не должен отправляться.");
            }
        }
    }
}
