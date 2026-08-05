#nullable enable

using Jellyfin.Plugin.Kinopoisk.Presentation;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskReviewsTests
    {
        [Fact]
        public void ShouldNormalizeReviewResponse()
        {
            const string json = """
                {
                  "total": 21,
                  "totalPages": 2,
                  "totalPositiveReviews": 10,
                  "totalNegativeReviews": 5,
                  "totalNeutralReviews": 6,
                  "items": [
                    {
                      "kinopoiskId": 101,
                      "type": "POSITIVE",
                      "date": "2026-01-02T03:04:05",
                      "positiveRating": 12,
                      "negativeRating": 3,
                      "author": "<b>Автор</b>",
                      "title": "<i>Заголовок</i>",
                      "description": "Текст <a href=\"/name/1/\">ссылки</a>."
                    }
                  ]
                }
                """;

            var result = KinopoiskSupplementalApiClient.MapReviews(json, 1);

            Assert.Equal(21, result.Total);
            Assert.Equal(2, result.TotalPages);
            Assert.True(result.HasNextPage);
            Assert.Equal(10, result.TotalPositiveReviews);
            Assert.Equal(5, result.TotalNegativeReviews);
            Assert.Equal(6, result.TotalNeutralReviews);
            var review = Assert.Single(result.Items);
            Assert.Equal(101, review.KinopoiskId);
            Assert.Equal("POSITIVE", review.Type);
            Assert.StartsWith("2026-01-02T03:04:05", review.Date);
            Assert.Equal(12, review.PositiveRating);
            Assert.Equal(3, review.NegativeRating);
            Assert.Equal("Автор", review.Author);
            Assert.Equal("Заголовок", review.Title);
            Assert.Equal("Текст ссылки.", review.Description);
        }

        [Fact]
        public void ShouldDiscardEmptyReviewsAndNormalizeValues()
        {
            const string json = """
                {
                  "total": 1,
                  "totalPages": 1,
                  "items": [
                    {
                      "kinopoiskId": 102,
                      "type": "UNSUPPORTED",
                      "date": "invalid",
                      "positiveRating": -1,
                      "negativeRating": -2,
                      "author": "Автор",
                      "title": null,
                      "description": "<script>alert(1)</script>"
                    }
                  ]
                }
                """;

            var result = KinopoiskSupplementalApiClient.MapReviews(json, 1);

            Assert.Empty(result.Items);
            Assert.Equal(1, result.Total);
            Assert.False(result.HasNextPage);
        }

        [Theory]
        [InlineData(null, "USER_POSITIVE_RATING_DESC")]
        [InlineData("", "USER_POSITIVE_RATING_DESC")]
        [InlineData("date_desc", "DATE_DESC")]
        [InlineData("USER_NEGATIVE_RATING_ASC", "USER_NEGATIVE_RATING_ASC")]
        [InlineData("unsupported", "USER_POSITIVE_RATING_DESC")]
        public void ShouldNormalizeReviewOrder(string? source, string expected)
        {
            Assert.Equal(expected, KinopoiskSupplementalApiClient.NormalizeReviewOrder(source));
        }
    }
}
