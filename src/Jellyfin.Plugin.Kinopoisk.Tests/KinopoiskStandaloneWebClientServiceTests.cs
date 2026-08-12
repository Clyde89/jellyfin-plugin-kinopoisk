using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskStandaloneWebClientServiceTests
    {
        private const string Digest =
            "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        [Fact]
        public void ShouldBuildManagedIndexIdempotently()
        {
            const string original = "<html><body><main>Jellyfin</main></body></html>";

            var first = KinopoiskStandaloneWebClientService.BuildManagedIndex(
                original,
                Digest);
            var second = KinopoiskStandaloneWebClientService.BuildManagedIndex(
                first,
                Digest);

            Assert.Equal(first, second);
            Assert.Contains(KinopoiskStandaloneWebClientService.BeginMarker, first);
            Assert.Contains(KinopoiskStandaloneWebClientService.EndMarker, first);
            Assert.Contains(
                "../Kinopoisk/WebClient.js?v=0123456789abcdef",
                first,
                StringComparison.Ordinal);
            Assert.DoesNotContain("kinopoisk-web-client.js", first, StringComparison.Ordinal);
            Assert.True(
                first.IndexOf(
                    KinopoiskStandaloneWebClientService.BeginMarker,
                    StringComparison.Ordinal)
                < first.IndexOf("</body>", StringComparison.OrdinalIgnoreCase));
        }

        [Fact]
        public void ShouldBuildExternallyManagedIndexIdempotently()
        {
            const string original = "<html><body><main>Jellyfin</main></body></html>";

            var first = KinopoiskStandaloneWebClientService.BuildExternallyManagedIndex(original);
            var second = KinopoiskStandaloneWebClientService.BuildExternallyManagedIndex(first);

            Assert.Equal(first, second);
            Assert.True(KinopoiskStandaloneWebClientService.IsExternallyManagedIndex(first));
            Assert.Contains(
                "../Kinopoisk/WebClient.js\" defer data-kinopoisk-managed=\"external\"",
                first,
                StringComparison.Ordinal);
            Assert.DoesNotContain("?v=", first, StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldRejectExternalAttributeOutsideManagedBlock()
        {
            const string original =
                "<html><body data-kinopoisk-managed=\"external\">"
                + "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->"
                + "<script src=\"../Kinopoisk/WebClient.js\" defer></script>"
                + "<!-- KINOPOISK_WEB_CLIENT_END -->"
                + "</body></html>";
            var method = typeof(KinopoiskStandaloneWebClientService).GetMethod(
                "VerifyExternalManagedIndex",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            var exception = Assert.Throws<TargetInvocationException>(
                () => method!.Invoke(null, new object[] { original }));
            Assert.IsType<InvalidDataException>(exception.InnerException);
        }

        [Fact]
        public void ShouldRemoveManagedBlockWithoutChangingBaseDocument()
        {
            const string original = "<html><body><main>Jellyfin</main></body></html>";
            var installed = KinopoiskStandaloneWebClientService.BuildManagedIndex(
                original,
                Digest);

            var clean = KinopoiskStandaloneWebClientService.RemoveManagedBlock(installed);

            Assert.Equal(original, clean);
        }

        [Fact]
        public void ShouldRejectMalformedManagedBlock()
        {
            var malformed = string.Concat(
                "<html><body>",
                KinopoiskStandaloneWebClientService.BeginMarker,
                "</body></html>");

            Assert.Throws<InvalidDataException>(
                () => KinopoiskStandaloneWebClientService.RemoveManagedBlock(malformed));
        }

        [Fact]
        public void ShouldRejectIndexWithoutBodyEnd()
        {
            Assert.Throws<InvalidDataException>(
                () => KinopoiskStandaloneWebClientService.BuildManagedIndex(
                    "<html><body>",
                    Digest));
        }

        [Fact]
        public void ShouldAssembleEmbeddedWebClientBundle()
        {
            Assert.Equal(12, KinopoiskWebClientBundle.ResourceCount);
            Assert.Equal(64, KinopoiskWebClientBundle.Sha256.Length);
            Assert.Equal(
                KinopoiskWebClientBundle.Sha256[..16],
                KinopoiskWebClientBundle.VersionToken);
            Assert.Equal(
                Encoding.UTF8.GetBytes(KinopoiskWebClientBundle.Content),
                KinopoiskWebClientBundle.Bytes);
            Assert.Contains(".btnPlayTrailer", KinopoiskWebClientBundle.Content);
            Assert.Contains(
                "Единый жизненный цикл карточки зарегистрирован",
                KinopoiskWebClientBundle.Content);
            Assert.Contains("Похожие и рекомендации", KinopoiskWebClientBundle.Content);
            Assert.Contains("kp-recommendations-scroller", KinopoiskWebClientBundle.Content);
            Assert.Contains(
                "Резервное сопоставление карточек через Seerr зарегистрировано",
                KinopoiskWebClientBundle.Content);
            Assert.Contains(
                "Объединённый блок рекомендаций зарегистрирован",
                KinopoiskWebClientBundle.Content);
    Assert.Contains(
        "Геометрическое выравнивание навигации зарегистрировано",
        KinopoiskWebClientBundle.Content);
        }

        [Fact]
        public void ShouldNotReferenceJavaScriptInjectorAssembly()
        {
            var references = typeof(KinopoiskStandaloneWebClientService)
                .Assembly
                .GetReferencedAssemblies();

            Assert.DoesNotContain(
                references,
                reference => reference.Name?.Contains(
                    "JavaScriptInjector",
                    StringComparison.OrdinalIgnoreCase) == true);
        }

        [Fact]
        public void ShouldContainIndexProtectionAndLegacyMigration()
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
                    "KinopoiskStandaloneWebClientService.cs"));

            Assert.Contains("VerifyInstallation", source);
            Assert.Contains("VerifyExternalManagedIndex", source);
            Assert.Contains("TryRestorePreviousIndex", source);
            Assert.Contains("rollback.sh", source);
            Assert.Contains("manifest.json", source);
            Assert.Contains("UnregisterAllScriptsFromPlugin", source);
            Assert.Contains("AssemblyLoadContext.All", source);
            Assert.Contains("plugin-api", source);
            Assert.Contains("bind-mount-external-index-read-only", source);
            Assert.DoesNotContain("kinopoisk-web-client.js", source, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "using Jellyfin.Plugin.JavaScriptInjector",
                source,
                StringComparison.Ordinal);
        }
    }
}
