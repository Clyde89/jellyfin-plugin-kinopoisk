using System;
using System.IO;
using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskWebBootstrapTransformerTests
    {
        [Fact]
        public void ShouldBuildRuntimeIndexIdempotently()
        {
            const string original = "<html><body><main>Jellyfin</main></body></html>";

            var first = KinopoiskWebBootstrapTransformer.Transform(original);
            var second = KinopoiskWebBootstrapTransformer.Transform(first);

            Assert.Equal(first, second);
            Assert.Equal(
                1,
                KinopoiskWebBootstrapTransformer.CountOccurrences(
                    first,
                    KinopoiskWebBootstrapTransformer.WebClientPath));
            Assert.Contains(
                string.Concat("?v=", KinopoiskWebClientBundle.VersionToken),
                first,
                StringComparison.Ordinal);
            Assert.Contains(
                KinopoiskWebBootstrapTransformer.RuntimeManagedAttribute,
                first,
                StringComparison.Ordinal);
        }

        [Fact]
        public void ShouldMigrateExternalManagedBlockInMemory()
        {
            var external = KinopoiskStandaloneWebClientService.BuildExternallyManagedIndex(
                "<html><body><main>Jellyfin</main></body></html>");

            var transformed = KinopoiskWebBootstrapTransformer.Transform(external);

            Assert.DoesNotContain(
                KinopoiskStandaloneWebClientService.ExternalManagedAttribute,
                transformed,
                StringComparison.Ordinal);
            Assert.Contains(
                KinopoiskWebBootstrapTransformer.RuntimeManagedAttribute,
                transformed,
                StringComparison.Ordinal);
            Assert.Equal(
                1,
                KinopoiskWebBootstrapTransformer.CountOccurrences(
                    transformed,
                    KinopoiskWebBootstrapTransformer.WebClientPath));
        }

        [Fact]
        public void ShouldRejectMalformedOrUnmanagedBootstrap()
        {
            var malformed = string.Concat(
                "<html><body>",
                KinopoiskWebBootstrapTransformer.BeginMarker,
                "</body></html>");
            const string unmanaged =
                "<html><body><script src=\"../Kinopoisk/WebClient.js\"></script></body></html>";

            Assert.Throws<InvalidDataException>(
                () => KinopoiskWebBootstrapTransformer.Transform(malformed));
            Assert.Throws<InvalidDataException>(
                () => KinopoiskWebBootstrapTransformer.Transform(unmanaged));
        }

        [Fact]
        public void ShouldBuildContentSensitiveRuntimeEtag()
        {
            var first = KinopoiskWebBootstrapTransformer.ComputeEtag([1, 2, 3]);
            var second = KinopoiskWebBootstrapTransformer.ComputeEtag([1, 2, 4]);

            Assert.StartsWith("\"kp-", first);
            Assert.EndsWith("\"", first);
            Assert.NotEqual(first, second);
        }
    }
}
