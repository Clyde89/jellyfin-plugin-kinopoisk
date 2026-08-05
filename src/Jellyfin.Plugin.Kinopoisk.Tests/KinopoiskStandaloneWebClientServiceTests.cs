using System;
using System.IO;
using System.Linq;
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
                "kinopoisk-web-client.js?v=0123456789abcdef",
                first,
                StringComparison.Ordinal);
            Assert.True(
                first.IndexOf(
                    KinopoiskStandaloneWebClientService.BeginMarker,
                    StringComparison.Ordinal)
                < first.IndexOf("</body>", StringComparison.OrdinalIgnoreCase));
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
        public void ShouldCalculateStableSha256()
        {
            Assert.Equal(
                "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
                KinopoiskStandaloneWebClientService.ComputeSha256("abc"));
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
        public void ShouldContainTransactionalProtectionAndLegacyMigration()
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
            Assert.Contains("RestorePreviousInstallation", source);
            Assert.Contains("rollback.sh", source);
            Assert.Contains("manifest.json", source);
            Assert.Contains("UnregisterAllScriptsFromPlugin", source);
            Assert.Contains("AssemblyLoadContext.All", source);
            Assert.DoesNotContain(
                "using Jellyfin.Plugin.JavaScriptInjector",
                source,
                StringComparison.Ordinal);
        }
    }
}
