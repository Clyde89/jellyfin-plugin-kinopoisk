using System;
using System.IO;
using System.Text;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class WebTrailerIntegrationTests
    {
        private const string ResourceName =
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskWidgetTrailerPlayer.js";

        [Fact]
        public void ShouldEmbedSecureKinopoiskWidgetPlayer()
        {
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            Assert.Contains(ResourceName, assembly.GetManifestResourceNames());

            using var stream = assembly.GetManifestResourceStream(ResourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var script = reader.ReadToEnd();

            Assert.Contains(".btnPlayTrailer", script);
            Assert.Contains("widgets.kinopoisk.ru", script);
            Assert.Contains("document.addEventListener('click', onTrailerClick, true)", script);
            Assert.Contains("event.stopImmediatePropagation()", script);
            Assert.Contains("apiClient.getItem", script);
            Assert.Contains("allowfullscreen", script);
            Assert.Contains("noopener,noreferrer", script);
            Assert.Contains("strict-origin-when-cross-origin", script);
            Assert.Contains("supportedHosts.has(host)", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Authorization", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterHostedWebTrailerIntegrationService()
        {
            var services = new ServiceCollection();
            new KinopoiskPluginServiceRegistrator().RegisterServices(services, null!);

            Assert.Contains(
                services,
                descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType
                        == typeof(KinopoiskWebTrailerIntegrationService));
        }

        [Fact]
        public void ShouldStoreWebTrailerIntegrationStateThreadSafely()
        {
            KinopoiskWebTrailerIntegrationState.SetRegistered(false);
            Assert.False(KinopoiskWebTrailerIntegrationState.IsRegistered);

            KinopoiskWebTrailerIntegrationState.SetRegistered(true);
            Assert.True(KinopoiskWebTrailerIntegrationState.IsRegistered);

            KinopoiskWebTrailerIntegrationState.SetRegistered(false);
            Assert.False(KinopoiskWebTrailerIntegrationState.IsRegistered);
        }
    }
}
