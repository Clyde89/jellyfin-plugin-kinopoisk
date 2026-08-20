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
            Assert.Contains("background: rgba(0, 164, 220, 0.22);", page);
            Assert.Contains("text-decoration-color: #35c8ff;", page);
            Assert.Contains("focus-visible", page);
            Assert.Contains("общий предел не задан", page);
        }

        [Fact]
        public void ShouldVisuallyDistinguishSelectedConfigurationTab()
        {
            var page = ReadConfigurationPage();

            Assert.Contains(".kinopoiskTabButton[aria-selected=\"true\"]", page);
            Assert.Contains("background: #00a4dc;", page);
            Assert.Contains("color: #fff;", page);
            Assert.Contains("attr('aria-selected', diagnostics ? 'false' : 'true')", page);
            Assert.Contains("attr('aria-selected', diagnostics ? 'true' : 'false')", page);
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
            Assert.Contains("Веб-интеграция КиноПоиска", page);
        }

        [Fact]
        public void ShouldExposeUnifiedTrailerCacheModes()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("id=\"EnableNativeTrailerCache\"", page);
            Assert.Contains("id=\"NativeTrailerCacheClientScope\"", page);
            Assert.Contains("value=\"AllClients\"", page);
            Assert.Contains("value=\"AndroidTvOnly\"", page);
            Assert.Contains("id=\"NativeTrailerCachePopulationMode\"", page);
            Assert.Contains("value=\"OnDemand\"", page);
            Assert.Contains("value=\"DuringMetadataScan\"", page);
            Assert.Contains("id=\"NativeTrailerCachePlaybackWaitSeconds\"", page);
            Assert.Contains("Изменение этих параметров требует перезапуска Jellyfin", page);
        }

        [Fact]
        public void ShouldExposeTrailerCacheStatisticsAndYoutubeRouteBoundary()
        {
            var page = ReadConfigurationPage();

            Assert.Contains("id=\"TrailerCacheCurrentBytes\"", page);
            Assert.Contains("id=\"TrailerCacheFileCount\"", page);
            Assert.Contains("id=\"TrailerCacheHits\"", page);
            Assert.Contains("id=\"TrailerCacheMisses\"", page);
            Assert.Contains("/KinopoiskPlayback/cache/statistics", page);
            Assert.Contains("id=\"EnableYoutubeTrailerFallback\"", page);
            Assert.Contains("сетевой трафик идёт от клиентского устройства", page);
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
