using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskDetailPageLifecycleTests
    {
        [Fact]
        public void ShouldCentralizeSpaNavigationAndProtectedImages()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskDetailPageLifecycle.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("patchHistory('pushState')", script, StringComparison.Ordinal);
            Assert.Contains("patchHistory('replaceState')", script, StringComparison.Ordinal);
            Assert.Contains("getActivePage", script, StringComparison.Ordinal);
            Assert.Contains("AbortController", script, StringComparison.Ordinal);
            Assert.Contains("/KinopoiskPresentation/image?url=", script, StringComparison.Ordinal);
            Assert.Contains("applyProtectedImage", script, StringComparison.Ordinal);
            Assert.Contains("credentials: 'same-origin'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("setInterval", script, StringComparison.Ordinal);
        }
    }
}
