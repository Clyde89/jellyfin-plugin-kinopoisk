using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTrailerManifestParserTests
    {
        [Fact]
        public void ShouldExtractEscapedTrustedHlsUrl()
        {
            const string Html = """
                <script type="application/json">
                {"src":"https:\/\/trailers.s3.mds.yandex.net\/video\/master.m3u8?token=a\u0026expires=1"}
                </script>
                """;

            var result = KinopoiskTrailerManifestParser.Extract(Html);
            var manifest = Assert.Single(result);

            Assert.Equal(
                "https://trailers.s3.mds.yandex.net/video/master.m3u8?token=a&expires=1",
                manifest.AbsoluteUri);
        }

        [Fact]
        public void ShouldRejectHlsUrlFromUntrustedHost()
        {
            const string Html = "<script>https://widgets.kinopoisk.ru.example.test/master.m3u8</script>";

            var result = KinopoiskTrailerManifestParser.Extract(Html);

            Assert.Empty(result);
        }

        [Fact]
        public void ShouldDeduplicateSameManifest()
        {
            const string Html = """
                https://strm.yandex.ru/trailer/master.m3u8
                https://strm.yandex.ru/trailer/master.m3u8
                """;

            Assert.Single(KinopoiskTrailerManifestParser.Extract(Html));
        }

        [Fact]
        public void ShouldPreferAdaptiveMasterManifest()
        {
            const string Html = """
                https://strm.yandex.ru/trailer/360.m3u8
                https://strm.yandex.ru/trailer/master.m3u8
                https://strm.yandex.ru/trailer/1080.m3u8
                """;

            var result = KinopoiskTrailerManifestParser.Extract(Html);

            Assert.Equal("https://strm.yandex.ru/trailer/master.m3u8", result[0].AbsoluteUri);
            Assert.Equal("https://strm.yandex.ru/trailer/1080.m3u8", result[1].AbsoluteUri);
        }
    }
}
