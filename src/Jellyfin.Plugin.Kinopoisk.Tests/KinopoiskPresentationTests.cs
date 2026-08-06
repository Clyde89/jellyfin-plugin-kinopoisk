using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Presentation;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Newtonsoft.Json;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskPresentationTests
    {
        [Fact]
        public async Task ShouldBuildRatingsDatesProfessionsAndRelations()
        {
            var api = new FakeApiClient();
            var service = new KinopoiskPresentationService(
                api,
                api,
                api,
                NullLogger<KinopoiskPresentationService>.Instance);

            var result = await service.Get(6638363, CancellationToken.None);

            Assert.Equal(6638363, result.KinopoiskId);
            Assert.Equal("Астронавт", result.Name);
            Assert.Equal("The Astronaut", result.OriginalName);
            Assert.Equal("tt13964560", result.ImdbId);
            Assert.Equal(5.8, result.Ratings.Kinopoisk);
            Assert.Equal(28125, result.Ratings.KinopoiskVotes);
            Assert.Equal(4.8, result.Ratings.Imdb);
            Assert.Equal(7900, result.Ratings.ImdbVotes);
            Assert.Null(result.Ratings.RussianCritics);
            Assert.Single(result.ReleaseDates);
            Assert.Equal("WORLD_PREMIER", result.ReleaseDates[0].Type);
            Assert.Equal("2025-03-07", result.ReleaseDates[0].Date);
            Assert.Equal("США", result.ReleaseDates[0].Country);
            Assert.Contains(result.Professions, group => group.Key == "DIRECTOR");
            Assert.Contains(result.Professions, group => group.Key == "OPERATOR");
            Assert.Single(result.Relations);
            Assert.Equal("SEQUEL", result.Relations[0].RelationType);
        }

        [Fact]
        public async Task ShouldIgnoreOptionalEndpointFailure()
        {
            var api = new FakeApiClient(failOptionalEndpoints: true);
            var service = new KinopoiskPresentationService(
                api,
                api,
                api,
                NullLogger<KinopoiskPresentationService>.Instance);

            var result = await service.Get(6638363, CancellationToken.None);

            Assert.Empty(result.ReleaseDates);
            Assert.Empty(result.Professions);
            Assert.Empty(result.Relations);
            Assert.Equal("Астронавт", result.Name);
        }

        [Fact]
        public void ShouldEmbedSecurePresentationScript()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskEnhancedPresentation.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;
            Assert.Contains(resourceName, assembly.GetManifestResourceNames());

            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new System.IO.StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains("/KinopoiskPresentation/", script);
            Assert.DoesNotContain("/release_dates", script, StringComparison.Ordinal);
            Assert.DoesNotContain("mediaInfoItem-releaseDate", script, StringComparison.Ordinal);
            Assert.DoesNotContain("calendar_month", script, StringComparison.Ordinal);
            Assert.Contains("Факты и интересные детали", script);
            Assert.Contains("Бюджет и сборы", script);
            Assert.Contains("Награды и номинации", script);
            Assert.Contains("Участники и профессии", script);
            Assert.Contains("Связанные фильмы и франшизы", script);
            Assert.Contains("noopener noreferrer", script);
            Assert.Contains("textContent", script);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("kinopoiskapiunofficial.tech/api", script, StringComparison.OrdinalIgnoreCase);
        }

        private sealed class FakeApiClient :
            IKinopoiskApiClient,
            IKinopoiskDistributionApiClient,
            IKinopoiskRelationsApiClient
        {
            private readonly bool _failOptionalEndpoints;

            public FakeApiClient(bool failOptionalEndpoints = false)
            {
                _failOptionalEndpoints = failOptionalEndpoints;
            }

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                return Task.FromResult(new Film
                {
                    KinopoiskId = filmId,
                    ImdbId = "tt13964560",
                    NameRu = "Астронавт",
                    NameEn = string.Empty,
                    NameOriginal = "The Astronaut",
                    Year = 2025,
                    FilmLength = 90,
                    Slogan = string.Empty,
                    Description = "Описание",
                    ShortDescription = "Краткое описание",
                    RatingKinopoisk = 5.8,
                    RatingKinopoiskVoteCount = 28125,
                    RatingImdb = 4.8,
                    RatingImdbVoteCount = 7900,
                    RatingFilmCritics = 0,
                    RatingFilmCriticsVoteCount = 0,
                    RatingRfCritics = 0,
                    RatingRfCriticsVoteCount = 0,
                    WebUrl = "https://www.kinopoisk.ru/film/6638363/",
                    PosterUrl = "https://example.test/poster.jpg",
                    PosterUrlPreview = "https://example.test/poster-small.jpg",
                    CoverUrl = string.Empty,
                    LogoUrl = string.Empty,
                    ReviewsCount = 0,
                    RatingGoodReview = 0,
                    RatingGoodReviewVoteCount = 0,
                    RatingAwait = 0,
                    RatingAwaitCount = 0,
                    RatingMpaa = string.Empty,
                    RatingAgeLimits = "age16",
                    StartYear = 0,
                    EndYear = 0,
                    Serial = false,
                    ShortFilm = false,
                    Completed = false,
                    HasImax = false,
                    Has3D = false,
                    LastSync = "2026-07-10T17:23:24.956951",
                    Countries = Array.Empty<Country>(),
                    Genres = Array.Empty<Genre>()
                });
            }

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                if (_failOptionalEndpoints)
                    throw new InvalidOperationException("Тестовая ошибка участников.");

                const string json = """
                    [
                      {
                        "staffId": 1,
                        "nameRu": "Джессика Варлей",
                        "nameEn": "Jess Varley",
                        "posterUrl": "",
                        "professionText": "Режиссёр",
                        "professionKey": "DIRECTOR"
                      },
                      {
                        "staffId": 2,
                        "nameRu": "Дэйв Гарбетт",
                        "nameEn": "Dave Garbett",
                        "posterUrl": "",
                        "professionText": "Оператор",
                        "professionKey": "OPERATOR"
                      }
                    ]
                    """;
                var result = JsonConvert.DeserializeObject<ICollection<StaffResponse>>(json)
                    ?? Array.Empty<StaffResponse>();
                return Task.FromResult(result);
            }

            public Task<DistributionResponse> GetDistributions(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                if (_failOptionalEndpoints)
                    throw new InvalidOperationException("Тестовая ошибка дат.");

                return Task.FromResult(new DistributionResponse
                {
                    Total = 1,
                    Items = new Collection<Distribution>
                    {
                        new()
                        {
                            Type = DistributionType.WORLD_PREMIER,
                            SubType = DistributionSubType.DIGITAL,
                            Date = "2025-03-07",
                            ReRelease = false,
                            Country = new Country { Country1 = "США" },
                            Companies = Array.Empty<Company>()
                        }
                    }
                });
            }

            public Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelations(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                if (_failOptionalEndpoints)
                    throw new InvalidOperationException("Тестовая ошибка связей.");

                ICollection<FilmSequelsAndPrequelsResponse> result = new[]
                {
                    new FilmSequelsAndPrequelsResponse
                    {
                        FilmId = filmId + 1,
                        NameRu = "Продолжение",
                        NameEn = "Sequel",
                        NameOriginal = "Sequel",
                        PosterUrl = "https://kinopoiskapiunofficial.tech/images/posters/kp/1.jpg",
                        PosterUrlPreview = "https://kinopoiskapiunofficial.tech/images/posters/kp_small/1.jpg",
                        RelationType = FilmSequelsAndPrequelsResponseRelationType.SEQUEL
                    }
                };
                return Task.FromResult(result);
            }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();
        }
    }
}
