using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimeNativeStyleTests
    {
        [Fact]
        public void ShouldEmbedCarouselOnlyNativeRuntimeStyle()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeNativeStyle.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("emby-scrollbuttons kp-native-navigation", script);
            Assert.Contains("emby-scrollbuttons-button paper-icon-button-light", script);
            Assert.Contains("'material-icons '", script);
            Assert.Contains("'chevron_left'", script);
            Assert.Contains("'chevron_right'", script);
            Assert.Contains("navigation.hidden = !hasOverflow", script);
            Assert.Contains("maximum > 20", script);
            Assert.Contains("tmdb-reviews-section", script);
            Assert.DoesNotContain("kp-recommendations-section", script, StringComparison.Ordinal);
            Assert.DoesNotContain("kp-recommendations-scroller", script, StringComparison.Ordinal);
            Assert.DoesNotContain("@media(hover:hover)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("kp-runtime-release", script, StringComparison.Ordinal);
            Assert.DoesNotContain("mediaInfoItem-releaseDate", script, StringComparison.Ordinal);
            Assert.DoesNotContain("calendar_month", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterNativeStyleAfterRuntimeCorrections()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..", "..", "..", "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "Services",
                    "KinopoiskWebClientBundle.cs"));

            var correctionsIndex = source.IndexOf(
                "kinopoiskRuntimeUiCorrections.js",
                StringComparison.Ordinal);
            var nativeIndex = source.IndexOf(
                "kinopoiskRuntimeNativeStyle.js",
                StringComparison.Ordinal);

            Assert.True(correctionsIndex >= 0);
            Assert.True(nativeIndex > correctionsIndex);
        }
    }
}
