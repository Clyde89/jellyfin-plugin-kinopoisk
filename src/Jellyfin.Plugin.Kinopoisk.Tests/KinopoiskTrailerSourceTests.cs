using System.Collections.ObjectModel;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTrailerSourceTests
    {
        [Fact]
        public void ShouldSelectKinopoiskWidgetTrailer()
        {
            var response = new VideoResponse
            {
                Total = 1,
                Items = new Collection<VideoResponse_items>
                {
                    new()
                    {
                        Name = "Трейлер (дублированный)",
                        Site = VideoResponse_itemsSite.KINOPOISK_WIDGET,
                        Url = "http://widgets.kinopoisk.ru/discovery/trailer/123"
                    }
                }
            };

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());
            var trailer = Assert.Single(result);

            Assert.Equal("КиноПоиск — Трейлер (дублированный)", trailer.Name);
            Assert.Equal(
                "https://widgets.kinopoisk.ru/discovery/trailer/123",
                trailer.Url);
        }

        [Fact]
        public void ShouldRejectSpoofedKinopoiskWidgetHost()
        {
            var response = new VideoResponse
            {
                Total = 1,
                Items = new Collection<VideoResponse_items>
                {
                    new()
                    {
                        Name = "Трейлер",
                        Site = VideoResponse_itemsSite.KINOPOISK_WIDGET,
                        Url = "https://widgets.kinopoisk.ru.example.test/trailer/123"
                    }
                }
            };

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());

            Assert.Empty(result);
        }

        [Fact]
        public void ShouldSelectYandexDiskTrailerFromAllowedHost()
        {
            var response = new VideoResponse
            {
                Total = 1,
                Items = new Collection<VideoResponse_items>
                {
                    new()
                    {
                        Name = "Трейлер",
                        Site = VideoResponse_itemsSite.YANDEX_DISK,
                        Url = "https://disk.yandex.ru/i/example"
                    }
                }
            };

            var result = KinopoiskTrailerSelector.Select(
                response,
                new KinopoiskTrailerSelectionOptions());

            Assert.Single(result);
        }
    }
}
