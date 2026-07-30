using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class VideoResolverLookupTitleTests
    {
        [Theory]
        [InlineData("Shrek 2 (2004) [tmdbid-809]", "Shrek 2")]
        [InlineData("Shrek 2 [tmdbid-809] (2004)", "Shrek 2")]
        [InlineData("Shrek 2 (2004) {tmdb-809}", "Shrek 2")]
        [InlineData("Shrek 2 (2004) [imdbid-tt0298148] [tmdbid-809]", "Shrek 2")]
        public async Task ShouldRemoveJellyfinSuffixesBeforeSearch(string sourceName, string expectedSearchTitle)
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(5273, "Шрэк 2", "Shrek 2", "2004"))
            };
            var resolver = CreateResolver(apiClient);
            var info = new MovieInfo
            {
                Name = sourceName,
                Year = 2004
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(5273, result.ProviderId);
            Assert.Equal(expectedSearchTitle, apiClient.LastSearchKeyword);
            Assert.Equal(sourceName, info.Name);
        }

        [Fact]
        public async Task ShouldKeepParenthesizedTitleWhenItIsNotTheProductionYear()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(100, "Тест", "Test (Extended Cut)", "2004"))
            };
            var resolver = CreateResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Test (Extended Cut)",
                Year = 2004
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(100, result.ProviderId);
            Assert.Equal("Test (Extended Cut)", apiClient.LastSearchKeyword);
        }

        private static VideoResolver<MovieInfo> CreateResolver(IKinopoiskApiClient apiClient)
        {
            return new VideoResolver<MovieInfo>(
                apiClient,
                NullLogger<VideoResolver<MovieInfo>>.Instance);
        }

        private static FilmSearchResponse CreateSearchResult(params FilmSearchResponse_films[] films)
        {
            return new FilmSearchResponse
            {
                Keyword = "test",
                PagesCount = 1,
                SearchFilmsCountResult = films.Length,
                Films = films
            };
        }

        private static FilmSearchResponse_films CreateCandidate(
            int filmId,
            string nameRu,
            string nameEn,
            string year)
        {
            return new FilmSearchResponse_films
            {
                FilmId = filmId,
                NameRu = nameRu,
                NameEn = nameEn,
                Type = FilmSearchResponse_filmsType.FILM,
                Year = year
            };
        }

        private sealed class FakeKinopoiskApiClient : IKinopoiskApiClient
        {
            public FilmSearchResponse SearchResult { get; set; } = CreateSearchResult();

            public string LastSearchKeyword { get; private set; }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                LastSearchKeyword = keyword;
                return Task.FromResult(SearchResult);
            }
        }
    }
}
