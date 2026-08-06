using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationLifecycleGuardTests
    {
        [Fact]
        public void ShouldEmbedDirectBoundedLifecycleRecovery()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendations.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("retryDelays = [0, 120, 280, 520, 900, 1450, 2200, 3400, 5200, 8000]", script);
            Assert.Contains("expectedGeneration !== renderGeneration", script);
            Assert.Contains("currentItemCache", script);
            Assert.Contains("var page = getVisiblePage();", script);
            Assert.Contains("function findInsertionAnchor(page)", script);
            Assert.Contains("var anchor = findInsertionAnchor(page);", script);
            Assert.Contains("anchor.isConnected", script);
            Assert.Contains("scheduleImmediateRender", script);
            Assert.Contains("window.setInterval", script);
            Assert.Contains("2000", script);
            Assert.DoesNotContain(".libraryPage:not(.hide)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("setTimeout(renderCurrentItem, 350)", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldAlignNavigationOnDesktopAndMobile()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationLifecycleGuard.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("findReferenceNavigation", script);
            Assert.Contains(".emby-scrollbuttons:not(.kp-native-navigation)", script);
            Assert.Contains("getBoundingClientRect()", script);
            Assert.Contains("headerRect.right - referenceRect.right", script);
            Assert.Contains("referenceRect.left - headerRect.left", script);
            Assert.Contains("--kp-native-navigation-right-offset", script);
            Assert.Contains("--kp-native-navigation-left-offset", script);
            Assert.Contains("margin-right:var(--kp-native-navigation-right-offset,0px)!important", script);
            Assert.Contains("@media(max-width:900px)", script);
            Assert.Contains("justify-content:flex-start!important", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }
    }
}
