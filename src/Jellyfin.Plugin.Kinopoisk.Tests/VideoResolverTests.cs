using System;
using System.Collections.Generic;
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
    public class VideoResolverTests
    {
        [Fact]
        public async Task ShouldResolveStoredKinopoiskIdWithoutSearch()
        {
            var apiClient = new FakeKinopoiskApiClient();
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo();
            info.SetProviderId(Constants.ProviderId, "430");

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
            Assert.Equal(0, apiClient.SearchCalls);
        }

        [Fact]
        public async Task ShouldResolveKinopoiskIdFromPathWithoutSearch()
        {
            var apiClient = new FakeKinopoiskApiClient();
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Path = "/media/movies/Шрэк (2001) [kp-430]/movie.mkv",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
            Assert.Equal(0, apiClient.SearchCalls);
        }

        [Fact]
        public async Task ShouldResolveSingleCandidateWithMatchingYear()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(430, "Шрэк", "Shrek", "2001"),
                    CreateCandidate(5273, "Шрэк 2", "Shrek 2", "2004"))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
            Assert.Equal(1, apiClient.SearchCalls);
        }

        [Fact]
        public async Task ShouldResolveExactImdbMatch()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(100, "Тест", "Test", "2001"),
                    CreateCandidate(430, "Шрэк", "Shrek", "2001"))
            };
            apiClient.Films[100] = CreateFilm(100, "tt0000100", "Тест");
            apiClient.Films[430] = CreateFilm(430, "tt0126029", "Шрэк");

            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Year = 2001
            };
            info.SetProviderId(MetadataProvider.Imdb, "tt0126029");

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
            Assert.Equal(1, apiClient.SearchCalls);
            Assert.Equal(2, apiClient.GetSingleFilmCalls);
        }

        [Fact]
        public async Task ShouldRejectMultipleCandidatesWithMatchingYearWithoutImdb()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(100, "Тест", "Test", "2001"),
                    CreateCandidate(430, "Шрэк", "Shrek", "2001"))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
        }

        [Fact]
        public async Task ShouldRejectMultipleCandidatesWhenYearIsMissing()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(430, "Шрэк", "Shrek", "2001"),
                    CreateCandidate(5273, "Шрэк 2", "Shrek 2", "2004"))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк"
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
        }

        [Fact]
        public async Task ShouldResolveMovieCandidateByType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(100, "Шрэк", "Shrek", "2001", FilmSearchResponse_filmsType.TV_SHOW),
                    CreateCandidate(430, "Шрэк", "Shrek", "2001", FilmSearchResponse_filmsType.FILM))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
        }

        [Fact]
        public async Task ShouldResolveSeriesCandidateByType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(100, "Тест", "Test", "2001", FilmSearchResponse_filmsType.FILM),
                    CreateCandidate(430, "Тест", "Test", "2001", FilmSearchResponse_filmsType.TV_SHOW))
            };
            var resolver = CreateSeriesResolver(apiClient);
            var info = new SeriesInfo
            {
                Name = "Тест",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.True(result.IsSuccess);
            Assert.Equal(430, result.ProviderId);
        }

        [Fact]
        public async Task ShouldRejectUnknownMovieCandidateType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(430, "Шрэк", "Shrek", "2001", FilmSearchResponse_filmsType.UNKNOWN))
            };
            var resolver = CreateMovieResolver(apiClient);
            var info = new MovieInfo
            {
                Name = "Шрэк",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
        }

        [Fact]
        public async Task ShouldRejectUnknownSeriesCandidateType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                SearchResult = CreateSearchResult(
                    CreateCandidate(430, "Тест", "Test", "2001", FilmSearchResponse_filmsType.UNKNOWN))
            };
            var resolver = CreateSeriesResolver(apiClient);
            var info = new SeriesInfo
            {
                Name = "Тест",
                Year = 2001
            };

            var result = await resolver.TryResolve(info);

            Assert.False(result.IsSuccess);
            Assert.Equal(0, result.ProviderId);
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
            string year,
            FilmSearchResponse_filmsType type = FilmSearchResponse_filmsType.FILM)
        {
            return new FilmSearchResponse_films
            {
                FilmId = filmId,
                NameRu = nameRu,
                NameEn = nameEn,
                Type = type,
                Year = year
            };
        }

        private static Film CreateFilm(int filmId, string imdbId, string nameRu)
        {
            return new Film
            {
                KinopoiskId = filmId,
                ImdbId = imdbId,
                NameRu = nameRu
            };
        }

        private sealed class FakeKinopoiskApiClient : IKinopoiskApiClient
        {
            public FilmSearchResponse SearchResult { get; set; } = CreateSearchResult();

            public IDictionary<int, Film> Films { get; } = new Dictionary<int, Film>();

            public int SearchCalls { get; private set; }

            public int GetSingleFilmCalls { get; private set; }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            {
                GetSingleFilmCalls++;

                if (!Films.TryGetValue(filmId, out var film))
                {
                    throw new InvalidOperationException($"Фильм {filmId} не подготовлен для теста.");
                }

                return Task.FromResult(film);
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
                SearchCalls++;
                return Task.FromResult(SearchResult);
            }
        }
    }
}
