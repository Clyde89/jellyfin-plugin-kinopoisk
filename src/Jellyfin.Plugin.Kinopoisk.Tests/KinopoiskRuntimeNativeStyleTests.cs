using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimeNativeStyleTests
    {
        [Fact]
        public void ShouldEmbedNativeRuntimeStyle()
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
            Assert.DoesNotContain("@media(hover:hover)", script, StringComparison.Ordinal);
            Assert.Contains("background:transparent!important", script);
            Assert.Contains("border:0!important", script);
            Assert.Contains("источник: TMDB", script);
            Assert.Contains("источники: TMDB и КиноПоиск", script);
        }

        [Fact]
        public void ShouldRegisterNativeStyleAfterRuntimeCorrections()
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
                    "KinopoiskWebPresentationIntegrationService.cs"));

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
