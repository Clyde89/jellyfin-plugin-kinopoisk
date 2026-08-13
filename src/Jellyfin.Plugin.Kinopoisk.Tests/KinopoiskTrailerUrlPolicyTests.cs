using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTrailerUrlPolicyTests
    {
        [Theory]
        [InlineData("https://trailers.s3.mds.yandex.net/video/master.m3u8")]
        [InlineData("https://strm.yandex.ru/video/master.m3u8")]
        [InlineData("https://widgets.kinopoisk.ru/video/master.m3u8")]
        public void ShouldAllowTrustedManifestHosts(string value)
        {
            Assert.True(KinopoiskTrailerUrlPolicy.TryNormalizeManifestUrl(value, out var uri));
            Assert.NotNull(uri);
        }

        [Theory]
        [InlineData("http://strm.yandex.ru/video/master.m3u8")]
        [InlineData("https://127.0.0.1/master.m3u8")]
        [InlineData("https://strm.yandex.ru.example.test/master.m3u8")]
        [InlineData("https://user:secret@strm.yandex.ru/master.m3u8")]
        [InlineData("https://strm.yandex.ru:444/master.m3u8")]
        public void ShouldRejectUnsafeManifestUrls(string value)
        {
            Assert.False(KinopoiskTrailerUrlPolicy.TryNormalizeManifestUrl(value, out _));
        }
    }
}
