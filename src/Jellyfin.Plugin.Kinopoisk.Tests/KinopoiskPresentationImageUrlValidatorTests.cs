using Jellyfin.Plugin.Kinopoisk.Api;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskPresentationImageUrlValidatorTests
    {
        [Theory]
        [InlineData("https://kinopoiskapiunofficial.tech/images/posters/kp/1.jpg")]
        [InlineData("https://avatars.mds.yandex.net/get-kinopoisk-image/1/2/orig")]
        [InlineData("https://st.kp.yandex.net/images/film_iphone/iphone360_1.jpg")]
        [InlineData("https://www.kinopoisk.ru/images/poster.jpg")]
        public void ShouldAllowTrustedHttpsImageHosts(string value)
        {
            Assert.True(KinopoiskPresentationImageUrlValidator.TryNormalize(value, out var uri));
            Assert.NotNull(uri);
            Assert.Equal("https", uri!.Scheme);
        }

        [Theory]
        [InlineData("http://kinopoiskapiunofficial.tech/images/poster.jpg")]
        [InlineData("https://kinopoiskapiunofficial.tech:444/images/poster.jpg")]
        [InlineData("https://user:secret@kinopoisk.ru/images/poster.jpg")]
        [InlineData("https://kinopoisk.ru.example.test/images/poster.jpg")]
        [InlineData("https://127.0.0.1/poster.jpg")]
        [InlineData("file:///etc/passwd")]
        public void ShouldRejectUntrustedOrUnsafeImageUrls(string value)
        {
            Assert.False(KinopoiskPresentationImageUrlValidator.TryNormalize(value, out _));
        }
    }
}
