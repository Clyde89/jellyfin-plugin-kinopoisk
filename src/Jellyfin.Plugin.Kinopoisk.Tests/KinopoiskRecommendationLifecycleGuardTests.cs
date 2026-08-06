using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationLifecycleGuardTests
    {
        [Fact]
        public void ShouldEmbedBoundedLifecycleRecovery()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationLifecycleGuard.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("retryDelays = [0, 120, 280, 520, 900, 1450, 2200, 3400, 5200]", script);
            Assert.Contains("expectedGeneration !== generation", script);
            Assert.Contains("getCurrentItemId() !== itemId", script);
            Assert.Contains("#itemDetailPage:not(.hide)", script);
            Assert.DoesNotContain(".libraryPage:not(.hide)", script, StringComparison.Ordinal);
            Assert.Contains("section.dataset.itemId === itemId", script);
            Assert.Contains("document.dispatchEvent(new Event('viewshow'))", script);
            Assert.Contains("internalDispatch", script);
            Assert.Contains("window.setInterval", script);
            Assert.Contains("2500", script);
        }

        [Fact]
        public void ShouldAlignMobileNavigationWithNativeCarousels()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationLifecycleGuard.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("@media(max-width:600px)", script);
            Assert.Contains(
                ".kp-recommendations-header>.kp-native-navigation.emby-scrollbuttons",
                script);
            Assert.Contains("margin-right:2.5em!important", script);
            Assert.Contains("transform:none!important", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }
    }
}
