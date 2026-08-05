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
            Assert.Contains("Расширенная подборка", script);
            Assert.Contains("/similars", script);
            Assert.Contains("fetchSimilarMovies", script);
            Assert.Contains("fetchRecommendedMovies", script);
            Assert.Contains("fetchSimilarTvShows", script);
            Assert.Contains("fetchRecommendedTvShows", script);
            Assert.Contains("createJellyseerrCard", script);
            Assert.Contains("#similarCollapsible", script);
            Assert.Contains("removeLegacySeerrSections", script);
            Assert.Contains("KinopoiskTmdbResolver", script);
            Assert.DoesNotContain("ProviderIds.Tmdb =", script, StringComparison.Ordinal);
            Assert.DoesNotContain("updateItem", script, StringComparison.Ordinal);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterRecommendationsInStandaloneWebClient()
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
            var webClient = File.ReadAllText(
                Path.Combine(
                    projectDirectory,
                    "Services",
                    "KinopoiskStandaloneWebClientService.cs"));

            Assert.Contains(
                "AddHostedService<KinopoiskStandaloneWebClientService>()",
                registrator);
            Assert.Contains("kinopoiskRecommendations.js", webClient);
            Assert.Contains("new KinopoiskSimilarApiClient(", registrator);
        }
    }
}
