using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskReviewsIntegrationTests
    {
        [Fact]
        public void ShouldEmbedReviewsIntegrationScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskReviewsIntegration.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("/reviews?page=", script);
            Assert.Contains("USER_POSITIVE_RATING_DESC", script);
            Assert.Contains(".tmdb-reviews-section", script);
            Assert.Contains("КиноПоиск", script);
            Assert.Contains("TMDB", script);
            Assert.Contains("Пользователи", script);
            Assert.Contains("Показать ещё ", script);
            Assert.Contains("Загрузить следующие рецензии", script);
            Assert.Contains("SpoilerStripReviews", script);
            Assert.Contains("loadPage(section, state, 1)", script);
            Assert.Contains("kpReviewRevision", script);
            Assert.Contains("kp-carousel-navigation", script);
            Assert.Contains("chevron_left", script);
            Assert.Contains("chevron_right", script);
            Assert.Contains("requestAnimationFrame", script);
            Assert.Contains("textContent", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "kinopoiskapiunofficial.tech/api",
                script,
                StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterReviewsHostedService()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "KinopoiskPluginServiceRegistrator.cs"));

            Assert.Contains(
                "AddHostedService<KinopoiskWebReviewsIntegrationService>()",
                source);
            Assert.Contains("KinopoiskReviewCacheClient", source);
            Assert.Contains("cache", source);
            Assert.Contains("presentation", source);
            Assert.Contains("reviews", source);
        }
    }
}
