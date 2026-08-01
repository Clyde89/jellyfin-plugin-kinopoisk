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
            var page = ReadEmbeddedPage("Configuration.configPage.html");

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
            var page = ReadEmbeddedPage("Configuration.configPage.html");

            Assert.Contains("class=\"kinopoiskExternalLink\"", page);
            Assert.Contains("focus-visible", page);
            Assert.Contains("общий предел не задан", page);
        }

        [Fact]
        public void ShouldContainBoundedDiagnosticSessionControls()
        {
            var page = ReadEmbeddedPage("Configuration.diagnosticsPage.html");

            Assert.Contains("EnableDiagnosticMode", page);
            Assert.Contains("DiagnosticLogLevel", page);
            Assert.Contains("DiagnosticSessionHours", page);
            Assert.Contains("DiagnosticMaximumFileMegabytes", page);
            Assert.Contains("DiagnosticRetentionFiles", page);
            Assert.Contains("StartDiagnosticSession", page);
            Assert.Contains("StopDiagnosticSession", page);
            Assert.Contains("API-токен и заголовки авторизации не сохраняются", page);
            Assert.Contains("kinopoisk-diagnostic-", page);
        }

        private static string ReadEmbeddedPage(string suffix)
        {
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            var resourceName = assembly
                .GetManifestResourceNames()
                .Single(name => name.EndsWith(suffix));

            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        }
    }
}
