using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class VideoRemoteSearchServiceTests
    {
        [Fact]
        public async Task ShouldUseFilteredImdbBeforeTitleAndFallback()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResponses =
                {
                    CreateFilteredResult(
                        CreateFilteredCandidate(
                            5273,
                            "tt0298148",
                            "Шрэк 2",
                            "Shrek 2",
                            "Shrek 2",
                            2004,
                            "FILM"))
                }
            };
            var info = new MovieInfo
            {
                Name = "Shrek 2 (2004) [tmdbid-809]",
                Year = 2004
            };
            info.SetProviderId(MetadataProvider.Imdb, "tt0298148");

            var results = (await SearchMovie(apiClient, info)).ToArray();

            var result = Assert.Single(results);
            Assert.Equal("5273", GetProviderId(result, Constants.ProviderId));
            Assert.Equal("tt0298148", GetProviderId(result, MetadataProvider.Imdb.ToString()));
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(0, apiClient.KeywordSearchCalls);

            var query = Assert.Single(apiClient.Queries);
            Assert.Equal("tt0298148", query.ImdbId);
            Assert.Null(query.Keyword);
            Assert.Equal("FILM", query.Type);
        }

        [Fact]
        public async Task ShouldUseCleanTitleYearAndTypeForFilteredSearch()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResponses =
                {
                    CreateFilteredResult(
                        CreateFilteredCandidate(
                            5273,
                            "tt0298148",
                            "Шрэк 2",
                            "Shrek 2",
                            "Shrek 2",
                            2004,
                            "FILM"))
                }
            };
            var info = new MovieInfo
            {
                Name = "Shrek 2 (2004) [tmdbid-809]",
                Year = 2004
            };

            var results = (await SearchMovie(apiClient, info)).ToArray();

            var result = Assert.Single(results);
            Assert.Equal("Шрэк 2", result.Name);
            Assert.Equal(2004, result.ProductionYear);
            Assert.Equal("5273", GetProviderId(result, Constants.ProviderId));
            Assert.Equal("tt0298148", GetProviderId(result, MetadataProvider.Imdb.ToString()));
            Assert.Equal(0, apiClient.KeywordSearchCalls);

            var query = Assert.Single(apiClient.Queries);
            Assert.Equal("Shrek 2", query.Keyword);
            Assert.Equal(2004, query.YearFrom);
            Assert.Equal(2004, query.YearTo);
            Assert.Equal("FILM", query.Type);
        }

        [Fact]
        public async Task ShouldReturnAllExactFilteredMatchesForManualChoice()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResponses =
                {
                    CreateFilteredResult(
                        CreateFilteredCandidate(100, null, "Тест", "Test", "Test", 2004, "FILM"),
                        CreateFilteredCandidate(200, null, "Тест", "Test", "Test", 2004, "FILM"))
                }
            };
            var info = new MovieInfo
            {
                Name = "Test",
                Year = 2004
            };

            var results = (await SearchMovie(apiClient, info)).ToArray();

            Assert.Equal(2, results.Length);
            Assert.Equal(new[] { "100", "200" }, results
                .Select(result => GetProviderId(result, Constants.ProviderId))
                .OrderBy(id => id)
                .ToArray());
            Assert.Equal(0, apiClient.KeywordSearchCalls);
        }

        [Fact]
        public async Task ShouldUseNormalizedKeywordFallbackWhenFilteredSearchIsEmpty()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResponses =
                {
                    CreateFilteredResult()
                },
                KeywordResult = CreateKeywordResult(
                    new FilmSearchResponse_films
                    {
                        FilmId = 5273,
                        NameRu = "Шрэк 2",
                        NameEn = "Shrek 2",
                        Year = "2004",
                        Type = FilmSearchResponse_filmsType.FILM
                    })
            };
            var info = new MovieInfo
            {
                Name = "Shrek 2 (2004) [tmdbid-809]",
                Year = 2004
            };

            var results = (await SearchMovie(apiClient, info)).ToArray();

            Assert.Single(results);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(1, apiClient.KeywordSearchCalls);
            Assert.Equal("Shrek 2", apiClient.LastKeyword);
        }

        [Fact]
        public async Task ShouldUseAllTypeAndAcceptMiniSeries()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResponses =
                {
                    CreateFilteredResult(
                        CreateFilteredCandidate(
                            12345,
                            "tt1234567",
                            "Тестовый мини-сериал",
                            "Test Mini Series",
                            "Test Mini Series",
                            2024,
                            "MINI_SERIES"))
                }
            };
            var info = new SeriesInfo
            {
                Name = "Test Mini Series",
                Year = 2024
            };

            var results = (await VideoRemoteSearchService.Search(
                apiClient,
                NullLogger.Instance,
                info,
                CancellationToken.None)).ToArray();

            var result = Assert.Single(results);
            Assert.Equal("12345", GetProviderId(result, Constants.ProviderId));
            Assert.Equal("ALL", Assert.Single(apiClient.Queries).Type);
        }

        [Fact]
        public async Task ShouldUseStoredKinopoiskIdWithoutSearch()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                Film = new Film
                {
                    KinopoiskId = 5273,
                    ImdbId = "tt0298148",
                    NameRu = "Шрэк 2",
                    NameOriginal = "Shrek 2",
                    Year = 2004,
                    Type = FilmType.FILM
                }
            };
            var info = new MovieInfo
            {
                Name = "Пользовательское название",
                Year = 1999
            };
            info.SetProviderId(Constants.ProviderId, "5273");

            var results = (await SearchMovie(apiClient, info)).ToArray();

            var result = Assert.Single(results);
            Assert.Equal("5273", GetProviderId(result, Constants.ProviderId));
            Assert.Equal(1, apiClient.GetSingleFilmCalls);
            Assert.Equal(0, apiClient.FilteredSearchCalls);
            Assert.Equal(0, apiClient.KeywordSearchCalls);
        }

        private static Task<IEnumerable<RemoteSearchResult>> SearchMovie(
            IKinopoiskApiClient apiClient,
            MovieInfo info)
        {
            return VideoRemoteSearchService.Search(
                apiClient,
                NullLogger.Instance,
                info,
                CancellationToken.None);
        }

        private static string GetProviderId(
            IHasProviderIds source,
            string providerName)
        {
            Assert.True(source.TryGetProviderId(providerName, out var providerId));
            return providerId;
        }

        private static FilteredFilmSearchResponse CreateFilteredResult(
            params FilteredFilmSearchItem[] items)
        {
            return new FilteredFilmSearchResponse
            {
                Total = items.Length,
                TotalPages = items.Length > 0 ? 1 : 0,
                Items = items
            };
        }

        private static FilteredFilmSearchItem CreateFilteredCandidate(
            int kinopoiskId,
            string imdbId,
            string nameRu,
            string nameEn,
            string nameOriginal,
            int year,
            string type)
        {
            return new FilteredFilmSearchItem
            {
                KinopoiskId = kinopoiskId,
                ImdbId = imdbId,
                NameRu = nameRu,
                NameEn = nameEn,
                NameOriginal = nameOriginal,
                Year = year,
                Type = type,
                PosterUrlPreview = $"https://example.test/{kinopoiskId}.jpg"
            };
        }

        private static FilmSearchResponse CreateKeywordResult(
            params FilmSearchResponse_films[] films)
        {
            return new FilmSearchResponse
            {
                Keyword = "test",
                PagesCount = films.Length > 0 ? 1 : 0,
                SearchFilmsCountResult = films.Length,
                Films = films
            };
        }

        private sealed class FakeFilteredKinopoiskApiClient : IFilteredKinopoiskApiClient
        {
            public IList<FilteredFilmSearchResponse> FilteredResponses { get; set; } = new List<FilteredFilmSearchResponse>();

            public FilmSearchResponse KeywordResult { get; set; } = CreateKeywordResult();

            public Film Film { get; set; }

            public IList<FilmSearchQuery> Queries { get; } = new List<FilmSearchQuery>();

            public int FilteredSearchCalls { get; private set; }

            public int KeywordSearchCalls { get; private set; }

            public int GetSingleFilmCalls { get; private set; }

            public string LastKeyword { get; private set; }

            public Task<FilteredFilmSearchResponse> SearchFilms(
                FilmSearchQuery query,
                CancellationToken? cancellationToken = null)
            {
                FilteredSearchCalls++;
                Queries.Add(query);

                if (FilteredResponses.Count < 1)
                    return Task.FromResult(CreateFilteredResult());

                var response = FilteredResponses[0];
                FilteredResponses.RemoveAt(0);
                return Task.FromResult(response);
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                KeywordSearchCalls++;
                LastKeyword = keyword;
                return Task.FromResult(KeywordResult);
            }

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetSingleFilmCalls++;
                return Task.FromResult(Film);
            }

            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }
        }
    }
}
