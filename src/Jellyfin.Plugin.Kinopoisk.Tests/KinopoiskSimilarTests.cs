using Jellyfin.Plugin.Kinopoisk.Presentation;
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
        public void ShouldReturnEmptyResultForEmptyPayload()
        {
            var result = KinopoiskSimilarApiClient.MapResponse("{}", 100);

            Assert.Equal(0, result.Total);
            Assert.Empty(result.Items);
        }
    }
}
