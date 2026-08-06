using Jellyfin.Plugin.Kinopoisk.Presentation;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskSimilarTests
    {
        [Fact]
        public void ShouldNormalizeAndDeduplicateSimilarItems()
        {
            const string json = """
                {
                  "total": 4,
                  "items": [
                    {
                      "filmId": 200,
                      "nameRu": "<b>Похожий фильм</b>",
                      "nameEn": "Similar Film",
                      "posterUrl": "https://example.test/poster.jpg",
                      "posterUrlPreview": "https://example.test/poster-small.jpg"
                    },
                    {
                      "filmId": 200,
                      "nameRu": "Дубликат",
                      "nameEn": "Duplicate"
                    },
                    {
                      "filmId": 100,
                      "nameRu": "Исходный фильм"
                    },
                    {
                      "filmId": 0,
                      "nameRu": "Некорректный фильм"
                    }
                  ]
                }
                """;

            var result = KinopoiskSimilarApiClient.MapResponse(json, 100);

            var item = Assert.Single(result.Items);
            Assert.Equal(1, result.Total);
            Assert.Equal(200, item.KinopoiskId);
            Assert.Equal("Похожий фильм", item.Name);
            Assert.Equal("Similar Film", item.OriginalName);
            Assert.Equal("https://www.kinopoisk.ru/film/200/", item.KinopoiskUrl);
        }

        [Fact]
        public void ShouldMapDetailedRecommendationFields()
        {
            var source = new KinopoiskSimilarInfo
            {
                KinopoiskId = 200,
                Name = "Похожий фильм",
                PosterUrl = "https://example.test/poster.jpg",
                PosterUrlPreview = "https://example.test/poster-small.jpg",
                KinopoiskUrl = "https://www.kinopoisk.ru/film/200/"
            };
            var film = new Film
            {
                KinopoiskId = 200,
                NameRu = "<b>Обогащённый фильм</b>",
                NameOriginal = "Enriched Film",
                PosterUrl = "https://example.test/poster-large.jpg",
                PosterUrlPreview = "https://example.test/poster-preview.jpg",
                Year = 2024,
                RatingKinopoisk = 7.4,
                ShortDescription = "<i>Краткое описание</i>",
                Description = "Полное описание",
                ImdbId = "tt1234567",
                Serial = false
            };

            var item = KinopoiskSimilarApiClient.MapDetails(source, film);

            Assert.Equal(200, item.KinopoiskId);
            Assert.Equal("Обогащённый фильм", item.Name);
            Assert.Equal("Enriched Film", item.OriginalName);
            Assert.Equal(2024, item.Year);
            Assert.Equal(7.4, item.RatingKinopoisk);
            Assert.Equal("Краткое описание", item.Overview);
            Assert.Equal("tt1234567", item.ImdbId);
            Assert.Equal("movie", item.MediaType);
            Assert.Equal("https://example.test/poster-large.jpg", item.PosterUrl);
            Assert.Equal("https://example.test/poster-preview.jpg", item.PosterUrlPreview);
        }

        [Fact]
        public void ShouldMapSeriesAndRejectInvalidImdbId()
        {
            var source = new KinopoiskSimilarInfo
            {
                KinopoiskId = 300,
                Name = "Сериал"
            };
            var film = new Film
            {
                KinopoiskId = 300,
                NameRu = "Сериал",
                StartYear = 2020,
                RatingKinopoisk = 8.1,
                Description = "Описание сериала",
                ImdbId = "invalid",
                Serial = true
            };

            var item = KinopoiskSimilarApiClient.MapDetails(source, film);

            Assert.Equal(2020, item.Year);
            Assert.Equal("tv", item.MediaType);
            Assert.Equal(string.Empty, item.ImdbId);
            Assert.Equal("Описание сериала", item.Overview);
        }

        [Fact]
        public void ShouldReturnEmptyResultForEmptyPayload()
        {
            var result = KinopoiskSimilarApiClient.MapResponse("{}", 100);

            Assert.Equal(0, result.Total);
            Assert.Empty(result.Items);
        }
    }
}
