using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskRecommendationScrollbarStyleTests
    {
        [Fact]
        public void ShouldEmbedRecommendationScrollbarStyle()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationScrollbarStyle.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains(".kp-recommendations-scroller", script);
            Assert.Contains("scrollbar-width:none", script);
            Assert.Contains("-ms-overflow-style:none", script);
            Assert.Contains("::-webkit-scrollbar", script);
            Assert.Contains("display:none", script);
            Assert.DoesNotContain("overflow-x:hidden", script, StringComparison.Ordinal);
            Assert.DoesNotContain("pointer-events:none", script, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRegisterScrollbarStyleAfterNativeRuntimeStyle()
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

            var nativeIndex = source.IndexOf(
                "kinopoiskRuntimeNativeStyle.js",
                StringComparison.Ordinal);
            var scrollbarIndex = source.IndexOf(
                "kinopoiskRecommendationScrollbarStyle.js",
                StringComparison.Ordinal);

            Assert.True(nativeIndex >= 0);
            Assert.True(scrollbarIndex > nativeIndex);
        }
    }
}
