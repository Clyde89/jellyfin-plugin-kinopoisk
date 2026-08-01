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

        [Fact]
        public void ShouldContainSettingsAndDiagnosticsOnSinglePage()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("KinopoiskSettingsTab", page);
            Assert.Contains("KinopoiskDiagnosticsTab", page);
            Assert.Contains("KinopoiskSettingsPanel", page);
            Assert.Contains("KinopoiskDiagnosticsPanel", page);
            Assert.Contains("EnableDiagnosticMode", page);
            Assert.Contains("DiagnosticLogLevel", page);
            Assert.Contains("DiagnosticSessionHours", page);
            Assert.Contains("DiagnosticMaximumFileMegabytes", page);
            Assert.Contains("DiagnosticRetentionFiles", page);
            Assert.Contains("StartDiagnosticSession", page);
            Assert.Contains("StopDiagnosticSession", page);
            Assert.Contains("API-токен и заголовки авторизации не сохраняются", page);
            Assert.Contains("kinopoisk-diagnostic-", page);
            Assert.Contains("id=\"ApiToken\"", page);
            Assert.Contains("id=\"EnableMovieMetadata\"", page);
            Assert.Contains("id=\"EnablePersistentCache\"", page);
            Assert.Contains("id=\"EnableImageBinaryCache\"", page);
        }

        [Fact]
        public void ShouldRefreshLocalProviderStatusEverySixtySecondsOnlyWhileVisible()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("refreshIntervalMilliseconds = 60000", page);
            Assert.Contains("document.visibilityState !== 'hidden'", page);
            Assert.Contains("pagehide.kinopoiskRuntimeStatus", page);
            Assert.Contains("visibilitychange", page);
            Assert.Contains("ApiClient.getPluginConfiguration(pluginUniqueId)", page);
            Assert.Contains("не расходует квоту КиноПоиска", page);
            Assert.DoesNotContain("GetApiQuota", page);
        }

        [Fact]
        public void ShouldEmbedOnlyOneConfigurationPage()
        {
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            var pages = assembly
                .GetManifestResourceNames()
                .Where(name => name.EndsWith("Page.html"))
                .ToArray();

            Assert.Single(pages);
            Assert.EndsWith("Configuration.configPage.html", pages[0]);
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
