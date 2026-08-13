using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationLifecycleTests
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
            Assert.Contains("handleLifecycleContext", script);
            Assert.Contains("lifecycle.subscribe(handleLifecycleContext)", script);
            Assert.DoesNotContain("window.setInterval", script, StringComparison.Ordinal);
            Assert.DoesNotContain(".libraryPage:not(.hide)", script, StringComparison.Ordinal);
            Assert.DoesNotContain("setTimeout(renderCurrentItem, 350)", script, StringComparison.Ordinal);
        }
    }
}
