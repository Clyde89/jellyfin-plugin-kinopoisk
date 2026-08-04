using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskPresentationScriptPolishTests
    {
        [Fact]
        public void ShouldRenderOnlyPrimaryPremieresInMainPresentation()
        {
            var script = ReadPresentationScript();

            Assert.Contains("selectPrimaryKinopoiskDates", script);
            Assert.Contains("Мировая премьера", script);
            Assert.Contains("Премьера в России", script);
            Assert.Contains("Все даты", script);
            Assert.Contains("barDates = tmdbDates.length ? tmdbDates : primaryDates", script);
        }

        [Fact]
        public void ShouldSupportIncrementalProfessionExpansion()
        {
            var script = ReadPresentationScript();

            Assert.Contains("var pageSize = 10", script);
            Assert.Contains("Показать ещё ", script);
            Assert.Contains("Свернуть", script);
            Assert.Contains("aria-expanded", script);
        }

        [Fact]
        public void ShouldNotDuplicateProviderLinksInPresentationHeader()
        {
            var script = ReadPresentationScript();

            Assert.DoesNotContain("КиноПоиск ↗", script, StringComparison.Ordinal);
            Assert.DoesNotContain("IMDb ↗", script, StringComparison.Ordinal);
        }

        private static string ReadPresentationScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskEnhancedPresentation.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            return reader.ReadToEnd();
        }
    }
}
