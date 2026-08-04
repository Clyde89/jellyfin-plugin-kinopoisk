using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRuntimeUiCorrectionsTests
    {
        [Fact]
        public void ShouldEmbedRuntimeUiCorrections()
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
            Assert.Contains("kp-runtime-release-row", script);
            Assert.Contains("kp-runtime-release-marker", script);
            Assert.Contains("primary.parentNode.insertBefore(row, primary.nextSibling)", script);
            Assert.Contains("Кино", script);
            Assert.Contains("Цифра", script);
            Assert.Contains("Носитель", script);
            Assert.Contains("/release_dates", script);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterRuntimeUiCorrectionsAfterExistingRuntimeScripts()
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

            var polishIndex = source.IndexOf(
                "kinopoiskRuntimePolish.js",
                StringComparison.Ordinal);
            var rebindIndex = source.IndexOf(
                "kinopoiskCarouselRebind.js",
                StringComparison.Ordinal);
            var correctionsIndex = source.IndexOf(
                "kinopoiskRuntimeUiCorrections.js",
                StringComparison.Ordinal);

            Assert.True(polishIndex >= 0);
            Assert.True(rebindIndex > polishIndex);
            Assert.True(correctionsIndex > rebindIndex);
        }
    }
}
