using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimePolishTests
    {
        [Fact]
        public void ShouldEmbedRuntimePolishScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimePolish.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("kp-runtime-navigation", script);
            Assert.Contains("chevron_left", script);
            Assert.Contains("chevron_right", script);
            Assert.Contains("ArrowLeft", script);
            Assert.Contains("ArrowRight", script);
            Assert.Contains("scrollBy", script);
            Assert.Contains("scrollbar-width:none", script);
            Assert.Contains("KinopoiskTmdbResolver", script);
            Assert.Contains("releaseCache.delete(itemId)", script);
            Assert.Contains("/release_dates", script);
            Assert.Contains("Кино", script);
            Assert.Contains("Цифра", script);
            Assert.Contains("Носитель", script);
            Assert.Contains("mediaInfoItem-releaseDate", script);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldEmbedCarouselRebindScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskCarouselRebind.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("kpBoundScrollerId", script);
            Assert.Contains("kp-recommendations-scroller", script);
            Assert.Contains("chevron_left", script);
            Assert.Contains("chevron_right", script);
            Assert.Contains("scrollBy", script);
        }

        [Fact]
        public void ShouldCombineRuntimePolishWithStandaloneClient()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "Services",
                    "KinopoiskStandaloneWebClientService.cs"));

            Assert.Contains("kinopoiskEnhancedPresentation.js", source);
            Assert.Contains("kinopoiskRuntimePolish.js", source);
            Assert.Contains("kinopoiskCarouselRebind.js", source);
            Assert.Contains("ReadEmbeddedScripts", source);
        }
    }
}
