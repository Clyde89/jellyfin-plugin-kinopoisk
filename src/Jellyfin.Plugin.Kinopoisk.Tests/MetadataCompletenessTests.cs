using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Model.Entities;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class MetadataCompletenessTests
    {
        [Fact]
        public void ShouldFillNativeMovieFieldsFromKinopoisk()
        {
            var film = new Film
            {
                KinopoiskId = 5273,
                ImdbId = "tt0298148",
                NameRu = "Шрэк 2",
                NameOriginal = "Shrek 2",
                Year = 2004,
                FilmLength = 93,
                Description = string.Empty,
                ShortDescription = "Шрэк и Фиона навещают родителей принцессы.",
                WebUrl = "https://www.kinopoisk.ru/film/5273/",
                RatingAgeLimits = "age6",
                RatingKinopoisk = 7.7,
                RatingImdb = 7.3,
                RatingRfCritics = 7.8,
                RatingFilmCritics = 7.4,
                PosterUrl = "https://example.test/poster.jpg",
                CoverUrl = "https://example.test/cover.jpg",
                LogoUrl = "https://example.test/logo.png"
            };

            var movie = film.ToMovie();

            Assert.Equal("Шрэк 2", movie.Name);
            Assert.Equal("Shrek 2", movie.OriginalTitle);
            Assert.Equal(2004, movie.ProductionYear);
            Assert.Equal(new DateTime(2004, 1, 1), movie.PremiereDate);
            Assert.Equal("Шрэк и Фиона навещают родителей принцессы.", movie.Overview);
            Assert.Equal("6+", movie.OfficialRating);
            Assert.Equal(7.7f, movie.CommunityRating);
            Assert.Equal(78f, movie.CriticRating);
            Assert.Equal(TimeSpan.FromMinutes(93).Ticks, movie.RunTimeTicks);
            Assert.Equal("https://www.kinopoisk.ru/film/5273/", movie.HomePageUrl);
            Assert.Equal("5273", movie.ProviderIds[Constants.ProviderId]);
            Assert.Equal("tt0298148", movie.ProviderIds["Imdb"]);

            var images = film.ToRemoteImageInfos().ToArray();
            Assert.Contains(images, image => image.Type == ImageType.Primary && image.Url.EndsWith("poster.jpg", StringComparison.Ordinal));
            Assert.Contains(images, image => image.Type == ImageType.Primary && image.Url.EndsWith("cover.jpg", StringComparison.Ordinal));
            Assert.Contains(images, image => image.Type == ImageType.Logo && image.Url.EndsWith("logo.png", StringComparison.Ordinal));
        }

        [Theory]
        [InlineData(CommunityRatingSource.KinopoiskWithImdbFallback, 8.1)]
        [InlineData(CommunityRatingSource.KinopoiskOnly, 8.1)]
        [InlineData(CommunityRatingSource.ImdbWithKinopoiskFallback, 7.4)]
        [InlineData(CommunityRatingSource.ImdbOnly, 7.4)]
        [InlineData(CommunityRatingSource.Disabled, null)]
        public void ShouldSelectConfiguredCommunityRating(
            CommunityRatingSource source,
            double? expected)
        {
            var film = new Film
            {
                RatingKinopoisk = 8.1,
                RatingImdb = 7.4
            };

            var actual = film.GetCommunityRating(source);

            if (expected.HasValue)
                Assert.Equal((float)expected.Value, actual);
            else
                Assert.Null(actual);
        }

        [Theory]
        [InlineData(CriticRatingSource.RussianWithWorldFallback, 78)]
        [InlineData(CriticRatingSource.RussianOnly, 78)]
        [InlineData(CriticRatingSource.WorldWithRussianFallback, 71)]
        [InlineData(CriticRatingSource.WorldOnly, 71)]
        [InlineData(CriticRatingSource.Disabled, null)]
        public void ShouldSelectAndScaleConfiguredCriticRating(
            CriticRatingSource source,
            double? expected)
        {
            var film = new Film
            {
                RatingRfCritics = 7.8,
                RatingFilmCritics = 7.1
            };

            var actual = film.GetCriticRatingAsPercentage(source);

            if (expected.HasValue)
                Assert.Equal((float)expected.Value, actual);
            else
                Assert.Null(actual);
        }

        [Fact]
        public void ShouldPreferWorldPremiereAndIgnoreReRelease()
        {
            var distributions = new DistributionResponse
            {
                Items = new List<Distribution>
                {
                    new Distribution
                    {
                        Type = DistributionType.PREMIERE,
                        SubType = DistributionSubType.CINEMA,
                        Date = "2004-05-20"
                    },
                    new Distribution
                    {
                        Type = DistributionType.WORLD_PREMIER,
                        SubType = DistributionSubType.CINEMA,
                        Date = "2004-05-19"
                    },
                    new Distribution
                    {
                        Type = DistributionType.WORLD_PREMIER,
                        SubType = DistributionSubType.CINEMA,
                        Date = "2001-01-01",
                        ReRelease = true
                    }
                }
            };

            Assert.Equal(new DateTime(2004, 5, 19), distributions.GetPrecisePremiereDate());
        }

        [Fact]
        public void ShouldFillEndedSeriesStatusAndYearRange()
        {
            var film = new Film
            {
                KinopoiskId = 100,
                NameRu = "Завершённый сериал",
                Type = FilmType.TV_SERIES,
                StartYear = 2020,
                EndYear = 2024,
                Completed = true,
                ProductionStatus = FilmProductionStatus.COMPLETED
            };

            var series = film.ToSeries();

            Assert.Equal(2020, series.ProductionYear);
            Assert.Equal(new DateTime(2024, 12, 31), series.EndDate);
            Assert.Equal(SeriesStatus.Ended, series.Status);
        }

        [Fact]
        public void ShouldFillUnreleasedSeriesStatus()
        {
            var film = new Film
            {
                KinopoiskId = 101,
                NameRu = "Анонсированный сериал",
                Type = FilmType.TV_SERIES,
                StartYear = DateTime.UtcNow.Year + 1,
                ProductionStatus = FilmProductionStatus.ANNOUNCED
            };

            var series = film.ToSeries();

            Assert.Equal(SeriesStatus.Unreleased, series.Status);
        }

        [Theory]
        [InlineData("2004-05-19", 2004, 5, 19)]
        [InlineData("2004-05-19T10:30:00Z", 2004, 5, 19)]
        public void ShouldParseKinopoiskDates(string value, int year, int month, int day)
        {
            Assert.Equal(new DateTime(year, month, day), value.ParseDate());
        }
    }
}
