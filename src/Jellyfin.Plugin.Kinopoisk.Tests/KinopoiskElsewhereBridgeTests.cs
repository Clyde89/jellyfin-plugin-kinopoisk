using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskElsewhereBridgeTests
    {
        [Fact]
        public void ShouldEmbedStandardFirstElsewhereBridge()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskElsewhereBridge.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("/JellyfinEnhanced/tmdb/find/", script);
            Assert.Contains("external_source=imdb_id", script);
            Assert.Contains("movie_results", script);
            Assert.Contains("tv_results", script);
            Assert.Contains(".streaming-lookup-container", script);
            Assert.Contains(".itemExternalLinks", script);
            Assert.Contains("kp-elsewhere-tmdb-bridge", script);
            Assert.Contains("link.hidden = true", script);
            Assert.Contains("ElsewhereEnabled", script);
            Assert.Contains("TmdbEnabled", script);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
            Assert.DoesNotContain("updateItem", script, StringComparison.Ordinal);
            Assert.DoesNotContain("api.themoviedb.org", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterElsewhereBridgeHostedService()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "KinopoiskPluginServiceRegistrator.cs"));

            Assert.Contains(
                "AddHostedService<KinopoiskWebElsewhereBridgeService>()",
                source);
        }
    }
}
