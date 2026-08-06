using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationSeerrFallbackTests
    {
        [Fact]
        public void ShouldEmbedStrictSeerrSearchFallback()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationSeerrFallback.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("var api = enhanced.jellyseerrAPI", script);
            Assert.Contains("api.search(query", script);
            Assert.Contains("selectUniqueMatch", script);
            Assert.Contains("candidateYear !== source.year", script);
            Assert.Contains("matches[0].score === matches[1].score", script);
            Assert.Contains("originalName", script);
            Assert.Contains("mediaType", script);
            Assert.Contains("KinopoiskTmdbResolver", script);
            Assert.Contains("createJellyseerrCard", script);
            Assert.Contains("kp-similar-card__fallback-button", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
            Assert.DoesNotContain("updateItem", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterFallbackImmediatelyAfterRecommendations()
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
                    "KinopoiskWebClientBundle.cs"));

            var recommendationsIndex = source.IndexOf(
                "kinopoiskRecommendations.js",
                StringComparison.Ordinal);
            var fallbackIndex = source.IndexOf(
                "kinopoiskRecommendationSeerrFallback.js",
                StringComparison.Ordinal);
            var runtimePolishIndex = source.IndexOf(
                "kinopoiskRuntimePolish.js",
                StringComparison.Ordinal);

            Assert.True(recommendationsIndex >= 0);
            Assert.True(fallbackIndex > recommendationsIndex);
            Assert.True(runtimePolishIndex > fallbackIndex);
        }
    }
}
