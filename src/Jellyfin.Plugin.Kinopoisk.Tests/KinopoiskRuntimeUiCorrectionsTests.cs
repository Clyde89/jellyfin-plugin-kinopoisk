using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimeUiCorrectionsTests
    {
        [Fact]
        public void ShouldEmbedNavigationOnlyRuntimeUiCorrections()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeUiCorrections.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("previous.textContent = '‹'", script);
            Assert.Contains("next.textContent = '›'", script);
            Assert.Contains("classList.remove('material-icons')", script);
            Assert.Contains("navigation.hidden = !hasOverflow", script);
            Assert.Contains("@media(hover:hover) and (pointer:fine)", script);
            Assert.Contains("tmdb-reviews-section", script);
            Assert.DoesNotContain("kp-recommendations-section", script, StringComparison.Ordinal);
            Assert.DoesNotContain("kp-recommendations-scroller", script, StringComparison.Ordinal);
            Assert.DoesNotContain("kp-runtime-release", script, StringComparison.Ordinal);
            Assert.DoesNotContain("mediaInfoItem-releaseDate", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/release_dates", script, StringComparison.Ordinal);
            Assert.DoesNotContain("calendar_month", script, StringComparison.Ordinal);
            Assert.DoesNotContain("KinopoiskTmdbResolver", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterRuntimeUiCorrectionsAfterExistingRuntimeScripts()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "Services",
                    "KinopoiskWebClientBundle.cs"));

            var polishIndex = source.IndexOf(
                "kinopoiskRuntimePolish.js",
                StringComparison.Ordinal);
            var correctionsIndex = source.IndexOf(
                "kinopoiskRuntimeUiCorrections.js",
                StringComparison.Ordinal);

            Assert.True(polishIndex >= 0);
            Assert.True(correctionsIndex > polishIndex);
            Assert.DoesNotContain("kinopoiskCarouselRebind.js", source);
        }
    }
}
