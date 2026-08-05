#nullable enable

using Jellyfin.Plugin.Kinopoisk.Presentation;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskPresentationTextSanitizerTests
    {
        [Fact]
        public void ShouldPreserveAnchorTextAndRemoveMarkup()
        {
            const string source =
                "На роль героини взяли <a href=\"/name/1/\">одну актрису</a>, "
                + "но позднее её сменила <a href=\"/name/2/\">другая актриса</a>.";

            var result = KinopoiskPresentationTextSanitizer.NormalizePlainText(source);

            Assert.Equal(
                "На роль героини взяли одну актрису, но позднее её сменила другая актриса.",
                result);
        }

        [Fact]
        public void ShouldDecodeEntitiesAndPreserveParagraphBoundaries()
        {
            const string source = "<p>Первый&nbsp;абзац.</p><p>Второй &amp; третий.</p>";

            var result = KinopoiskPresentationTextSanitizer.NormalizePlainText(source);

            Assert.Equal("Первый абзац.\n\nВторой & третий.", result);
        }

        [Fact]
        public void ShouldRemoveExecutableMarkup()
        {
            const string source =
                "Текст<script>alert('x')</script><img src=x onerror=alert(1)> продолжен.";

            var result = KinopoiskPresentationTextSanitizer.NormalizePlainText(source);

            Assert.DoesNotContain("<", result);
            Assert.DoesNotContain(">", result);
            Assert.DoesNotContain("onerror", result);
            Assert.DoesNotContain("alert", result);
            Assert.Equal("Текст продолжен.", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   ")]
        public void ShouldReturnEmptyTextForEmptyInput(string? source)
        {
            Assert.Equal(string.Empty, KinopoiskPresentationTextSanitizer.NormalizePlainText(source));
        }
    }
}
