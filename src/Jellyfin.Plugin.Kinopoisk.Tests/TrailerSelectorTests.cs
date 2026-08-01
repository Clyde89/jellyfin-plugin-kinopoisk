using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class TrailerSelectorTests
    {
        [Theory]
        [InlineData("https://www.youtube.com/watch?v=ABCDEFGHIJK")]
        [InlineData("http://m.youtube.com/watch?feature=share&v=ABCDEFGHIJK&t=30")]
        [InlineData("https://youtu.be/ABCDEFGHIJK?si=test")]
        [InlineData("https://www.youtube.com/embed/ABCDEFGHIJK?autoplay=1")]
        [InlineData("https://www.youtube.com/v/ABCDEFGHIJK")]
        [InlineData("https://www.youtube.com/shorts/ABCDEFGHIJK")]
        [InlineData("https://www.youtube-nocookie.com/embed/ABCDEFGHIJK")]
        public void ShouldNormalizeSupportedYoutubeUrls(string sourceUrl)
        {
            var result = KinopoiskTrailerSelector.TryNormalizeYoutubeUrl(
                sourceUrl,
                out var videoId,
                out var canonicalUrl);

            Assert.True(result);
            Assert.Equal("ABCDEFGHIJK", videoId);
            Assert.Equal("https://www.youtube.com/watch?v=ABCDEFGHIJK", canonicalUrl);
        }

        [Theory]
        [InlineData("https://example.test/watch?v=ABCDEFGHIJK")]
        [InlineData("https://youtube.example.test/watch?v=ABCDEFGHIJK")]
        [InlineData("file:///tmp/trailer.mp4")]
        [InlineData("javascript:alert(1)")]
        [InlineData("https://www.youtube.com/watch?v=bad id")]
        [InlineData("")]
        public void ShouldRejectUnsupportedOrUnsafeUrls(string sourceUrl)
        {
            Assert.False(KinopoiskTrailerSelector.TryNormalizeYoutubeUrl(
                sourceUrl,
                out _,
                out _));
        }

        [Fact]
        public void ShouldPrioritizeOfficialRussianTrailer()
        {
            var response = CreateResponse(
                Video("Teaser", "https://youtu.be/TEASER00001"),
                Video("Official Trailer", "https://youtu.be/ENGLISH0001"),
                Video("Официальный дублированный трейлер", "https://youtu.be/RUSSIAN0001"));

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());

            Assert.Equal(3, result.Count);
            Assert.Equal(
                "https://www.youtube.com/watch?v=RUSSIAN0001",
                result[0].Url);
            Assert.StartsWith("КиноПоиск — ", result[0].Name);
        }

        [Fact]
        public void ShouldDeduplicateSameYoutubeVideoIdAcrossUrlFormats()
        {
            var response = CreateResponse(
                Video("Трейлер", "https://youtu.be/DUPLICATE01"),
                Video("Официальный трейлер", "https://www.youtube.com/embed/DUPLICATE01"),
                Video("Другой трейлер", "https://www.youtube.com/watch?v=UNIQUE00001"));

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());

            Assert.Equal(2, result.Count);
            Assert.Single(result.Where(item => item.Url.EndsWith("DUPLICATE01")));
            Assert.Contains("Официальный", result.Single(item => item.Url.EndsWith("DUPLICATE01")).Name);
        }

        [Fact]
        public void ShouldIncludeSupportedKinopoiskYandexAndYoutubeSources()
        {
            var response = CreateResponse(
                Video(
                    "Виджет КиноПоиска",
                    "https://widgets.kinopoisk.ru/discovery/trailer/1",
                    VideoResponse_itemsSite.KINOPOISK_WIDGET),
                Video(
                    "Яндекс.Диск",
                    "https://disk.yandex.ru/i/example",
                    VideoResponse_itemsSite.YANDEX_DISK),
                Video("YouTube trailer", "https://youtu.be/YOUTUBE001"));

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());

            Assert.Equal(3, result.Count);
            Assert.Contains(result, item => item.Url.StartsWith("https://widgets.kinopoisk.ru/"));
            Assert.Contains(result, item => item.Url.StartsWith("https://disk.yandex.ru/"));
            Assert.Contains(result, item => item.Url == "https://www.youtube.com/watch?v=YOUTUBE001");
        }

        [Fact]
        public void ShouldRespectTeaserAdditionalVideoAndLimitSettings()
        {
            var response = CreateResponse(
                Video("Трейлер 1", "https://youtu.be/TRAILER0001"),
                Video("Трейлер 2", "https://youtu.be/TRAILER0002"),
                Video("Тизер", "https://youtu.be/TEASER00001"),
                Video("Фрагмент", "https://youtu.be/CLIP0000001"));

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions
                {
                    MaximumTrailers = 1,
                    IncludeTeasers = false,
                    IncludeAdditionalVideos = false,
                    PrefixTrailerNames = false
                });

            var trailer = Assert.Single(result);
            Assert.Equal("Трейлер 1", trailer.Name);
            Assert.DoesNotContain(result, item => item.Url.EndsWith("TEASER00001"));
            Assert.DoesNotContain(result, item => item.Url.EndsWith("CLIP0000001"));
        }

        [Fact]
        public void ShouldIncludeAdditionalVideosWhenEnabled()
        {
            var response = CreateResponse(
                Video("Интервью с актёрами", "https://youtu.be/INTERVIEW01"),
                Video("Фрагмент фильма", "https://youtu.be/FRAGMENT001"));

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions
                {
                    IncludeAdditionalVideos = true
                });

            Assert.Equal(2, result.Count);
        }

        private static VideoResponse CreateResponse(params VideoResponse_items[] items)
        {
            return new VideoResponse
            {
                Items = new List<VideoResponse_items>(items)
            };
        }

        private static VideoResponse_items Video(
            string name,
            string url,
            VideoResponse_itemsSite site = VideoResponse_itemsSite.YOUTUBE)
        {
            return new VideoResponse_items
            {
                Name = name,
                Url = url,
                Site = site
            };
        }
    }
}
