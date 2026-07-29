using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class VideoResolverFilteredSearchTests
    {
        [Fact]
        public async Task ShouldResolveByFilteredImdbBeforeKeywordFallback()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResult = CreateFilteredResult(
                    CreateFilteredCandidate(
                        5273,
                        "tt0298148",
                        "Шрэк 2",
                        "Shrek 2",
                        "Shrek 2",
                        2004,
                        "FILM"))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Shrek 2 (2004) [tmdbid-809]",
                Year = 2004
            };
            info.SetProviderId(MetadataProvider.Imdb, "tt0298148");

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(5273, result.ProviderId);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(0, apiClient.KeywordSearchCalls);

            var query = Assert.Single(apiClient.Queries);
            Assert.Equal("tt0298148", query.ImdbId);
            Assert.Null(query.Keyword);
            Assert.Equal("FILM", query.Type);
            Assert.Null(query.YearFrom);
            Assert.Null(query.YearTo);
        }

        [Fact]
        public async Task ShouldResolveByFilteredTitleYearAndType()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResult = CreateFilteredResult(
                    CreateFilteredCandidate(
                        5273,
                        "tt0298148",
                        "Шрэк 2",
                        "Shrek 2",
                        "Shrek 2",
                        2004,
                        "FILM"))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Shrek 2 (2004) [tmdbid-809]",
                Year = 2004
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(5273, result.ProviderId);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(0, apiClient.KeywordSearchCalls);

            var query = Assert.Single(apiClient.Queries);
            Assert.Null(query.ImdbId);
            Assert.Equal("Shrek 2", query.Keyword);
            Assert.Equal(2004, query.YearFrom);
            Assert.Equal(2004, query.YearTo);
            Assert.Equal("FILM", query.Type);
        }

        [Fact]
        public async Task ShouldRejectAmbiguousFilteredTitleAndUseFallback()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResult = CreateFilteredResult(
                    CreateFilteredCandidate(100, null, "Тест", "Test", "Test", 2004, "FILM"),
                    CreateFilteredCandidate(200, null, "Тест", "Test", "Test", 2004, "FILM")),
                KeywordResult = CreateKeywordResult()
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Test",
                Year = 2004
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(1, apiClient.KeywordSearchCalls);
        }

        [Fact]
        public async Task ShouldRejectFilteredCandidateWithIncompatibleType()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResult = CreateFilteredResult(
                    CreateFilteredCandidate(5273, null, "Шрэк 2", "Shrek 2", "Shrek 2", 2004, "TV_SERIES")),
                KeywordResult = CreateKeywordResult()
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Shrek 2",
                Year = 2004
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(1, apiClient.KeywordSearchCalls);
        }

        [Fact]
        public async Task ShouldResolveMiniSeriesFromFilteredSearch()
        {
            var apiClient = new FakeFilteredKinopoiskApiClient
            {
                FilteredResult = CreateFilteredResult(
                    CreateFilteredCandidate(
                        12345,
                        "tt1234567",
                        "Тестовый мини-сериал",
                        "Test Mini Series",
                        "Test Mini Series",
                        2024,
                        "MINI_SERIES"))
            };
            var resolver = CreateSeriesResolver(apiClient);
            var info = new SeriesInfo
            {
                Name = "Test Mini Series",
                Year = 2024
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(12345, result.ProviderId);
            Assert.Equal(1, apiClient.FilteredSearchCalls);
            Assert.Equal(0, apiClient.KeywordSearchCalls);

            var query = Assert.Single(apiClient.Queries);
            Assert.Equal("ALL", query.Type);
        }

        private static VideoResolver<MovieInfo> CreateMovieResolver(IKinopoiskApiClient apiClient)
        {
            return new VideoResolver<MovieInfo>(
                apiClient,
                NullLogger<VideoResolver<MovieInfo>>.Instance);
        }

        private static VideoResolver<SeriesInfo> CreateSeriesResolver(IKinopoiskApiClient apiClient)
        {
            return new VideoResolver<SeriesInfo>(
                apiClient,
                NullLogger<VideoResolver<SeriesInfo>>.Instance);
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
                Type = type
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
            public FilteredFilmSearchResponse FilteredResult { get; set; } = CreateFilteredResult();

            public FilmSearchResponse KeywordResult { get; set; } = CreateKeywordResult();

            public IList<FilmSearchQuery> Queries { get; } = new List<FilmSearchQuery>();

            public int FilteredSearchCalls { get; private set; }

            public int KeywordSearchCalls { get; private set; }

            public Task<FilteredFilmSearchResponse> SearchFilms(
                FilmSearchQuery query,
                CancellationToken? cancellationToken = null)
            {
                FilteredSearchCalls++;
                Queries.Add(query);
                return Task.FromResult(FilteredResult);
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                KeywordSearchCalls++;
                return Task.FromResult(KeywordResult);
            }

            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<Film> GetSingleFilm(
                int filmId,
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
