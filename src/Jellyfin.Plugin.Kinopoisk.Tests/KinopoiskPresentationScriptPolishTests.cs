using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskPresentationScriptPolishTests
    {
        [Fact]
        public void ShouldLeaveUpperReleaseDateOwnershipToJellyfinEnhanced()
        {
            var script = ReadPresentationScript();

            Assert.DoesNotContain("fetchTmdbReleaseDates", script, StringComparison.Ordinal);
            Assert.DoesNotContain("/release_dates", script, StringComparison.Ordinal);
            Assert.DoesNotContain("mediaInfoItem-releaseDate", script, StringComparison.Ordinal);
            Assert.Contains("showDatesDialog", script, StringComparison.Ordinal);
            Assert.Contains("kp-date-list", script, StringComparison.Ordinal);
            Assert.Contains("calendar_month", script, StringComparison.Ordinal);
            Assert.Contains("Все даты", script, StringComparison.Ordinal);
            Assert.Contains("if (window.JellyfinEnhanced || !primaryDates.length)", script, StringComparison.Ordinal);
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
