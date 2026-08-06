using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationsTests
    {
        [Fact]
        public void ShouldEmbedCombinedRecommendationsScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendations.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("Похожие и рекомендации", script);
            Assert.Contains("КиноПоиск", script);
            Assert.Contains("The Movie Database", script);
            Assert.DoesNotContain("Расширенная подборка", script, StringComparison.Ordinal);
            Assert.Contains("/similars", script);
            Assert.Contains("fetchSimilarMovies", script);
            Assert.Contains("fetchRecommendedMovies", script);
            Assert.Contains("fetchSimilarTvShows", script);
            Assert.Contains("fetchRecommendedTvShows", script);
            Assert.Contains("fetchMovieDetails", script);
            Assert.Contains("fetchTvShowDetails", script);
            Assert.Contains("getKinopoiskCardData", script);
            Assert.Contains("createKinopoiskFallbackCard", script);
            Assert.Contains("customizeKinopoiskCard", script);
            Assert.Contains("createJellyseerrCard", script);
            Assert.Contains("ratingKinopoisk", script);
            Assert.Contains("overview", script);
            Assert.Contains("imdbId", script);
            Assert.Contains("mediaType", script);
            Assert.Contains(".kp-recommendations-items>.card{width:12.4em", script);
            Assert.Contains("function findInsertionAnchor(page)", script);
            Assert.Contains("page.querySelector('#similarCollapsible')", script);
            Assert.Contains("page.querySelector('.similarCollapsible')", script);
            Assert.Contains("removeLegacySeerrSections", script);
            Assert.Contains("KinopoiskTmdbResolver", script);
            Assert.Contains("retryDelays", script);
            Assert.Contains("renderGeneration", script);
            Assert.Contains("currentItemCache", script);
            Assert.DoesNotContain(".libraryPage:not(.hide)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
            Assert.DoesNotContain("updateItem", script, StringComparison.Ordinal);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterRecommendationsInAutonomousBundle()
        {
            var projectDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Jellyfin.Plugin.Kinopoisk");
            var registrator = File.ReadAllText(
                Path.Combine(projectDirectory, "KinopoiskPluginServiceRegistrator.cs"));
            var bundle = File.ReadAllText(
                Path.Combine(
                    projectDirectory,
                    "Services",
                    "KinopoiskWebClientBundle.cs"));

            Assert.Contains(
                "AddHostedService<KinopoiskStandaloneWebClientService>()",
                registrator);
            Assert.Contains("kinopoiskRecommendations.js", bundle);
            Assert.Contains("new KinopoiskSimilarApiClient(", registrator);
            Assert.Contains(
                "sp.GetRequiredService<IKinopoiskApiClient>()",
                registrator);
        }
    }
}
