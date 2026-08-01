using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class ConfigurationPagePresentationTests
    {
        [Fact]
        public void ShouldUseEnumNamesForRatingSourceOptions()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("value=\"KinopoiskWithImdbFallback\"", page);
            Assert.Contains("value=\"KinopoiskOnly\"", page);
            Assert.Contains("value=\"ImdbWithKinopoiskFallback\"", page);
            Assert.Contains("value=\"ImdbOnly\"", page);
            Assert.Contains("value=\"RussianWithWorldFallback\"", page);
            Assert.Contains("value=\"RussianOnly\"", page);
            Assert.Contains("value=\"WorldWithRussianFallback\"", page);
            Assert.Contains("value=\"WorldOnly\"", page);
            Assert.DoesNotContain("value=\"0\">КиноПоиск, затем IMDb", page);
            Assert.DoesNotContain("value=\"0\">Критики России, затем мировые", page);
        }

        [Fact]
        public void ShouldContainAccessibleExternalLinkAndUnlimitedQuotaText()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("class=\"kinopoiskExternalLink\"", page);
            Assert.Contains("focus-visible", page);
            Assert.Contains("общий предел не задан", page);
        }

        private static string ReadConfigurationPage()
        {
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .Single(name => name.EndsWith("Configuration.configPage.html"));

            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
