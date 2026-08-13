using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimePolishTests
    {
        [Fact]
        public void ShouldEmbedCarouselOnlyRuntimePolishScript()
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
            Assert.Contains("tmdb-review-swipe-container", script);
            Assert.DoesNotContain("kp-recommendations-section", script, StringComparison.Ordinal);
            Assert.DoesNotContain("kp-recommendations-scroller", script, StringComparison.Ordinal);
            Assert.DoesNotContain("releaseCache", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/release_dates", script, StringComparison.Ordinal);
            Assert.DoesNotContain("mediaInfoItem-releaseDate", script, StringComparison.Ordinal);
            Assert.DoesNotContain("calendar_month", script, StringComparison.Ordinal);
            Assert.DoesNotContain("KinopoiskTmdbResolver", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldCombineRuntimePolishInEmbeddedBundle()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "Services",
                    "KinopoiskWebClientBundle.cs"));

            Assert.Contains("kinopoiskEnhancedPresentation.js", source);
            Assert.Contains("kinopoiskRuntimePolish.js", source);
            Assert.DoesNotContain("kinopoiskCarouselRebind.js", source);
            Assert.Contains("CreateBundle", source);
        }
    }
}
