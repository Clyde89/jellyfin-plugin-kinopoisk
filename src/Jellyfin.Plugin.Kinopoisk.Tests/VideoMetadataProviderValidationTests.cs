using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class VideoMetadataProviderValidationTests
    {
        [Fact]
        public async Task ShouldRejectFullCardWithUnexpectedKinopoiskId()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(430, "tt0126029", "Шрэк", "Shrek", 2001)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = CreateMovieInfo("Шрэк 2", 2004, "tt0298148");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.Null(result.Item);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
            Assert.Equal(1, apiClient.GetSingleFilmCalls);
            Assert.Equal(0, apiClient.GetStaffCalls);
            Assert.Equal(0, apiClient.GetTrailersCalls);
        }

        [Fact]
        public async Task ShouldRejectAutomaticallyResolvedCardWithDifferentImdbId()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(5273, "tt0000001", "Шрэк 2", "Shrek 2", 2004)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = CreateMovieInfo("Шрэк 2", 2004, "tt0298148");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
            Assert.Equal(0, apiClient.GetStaffCalls);
            Assert.Equal(0, apiClient.GetTrailersCalls);
        }

        [Fact]
        public async Task ShouldRejectAutomaticallyResolvedCardWithDifferentTitle()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(5273, null, "Другой фильм", "Another Film", 2004)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = CreateMovieInfo("Shrek 2 (2004) [tmdbid-809]", 2004);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
            Assert.Equal(0, apiClient.GetStaffCalls);
            Assert.Equal(0, apiClient.GetTrailersCalls);
        }

        [Fact]
        public async Task ShouldRejectAutomaticallyResolvedCardWithDifferentYear()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(5273, null, "Шрэк 2", "Shrek 2", 2005)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = CreateMovieInfo("Шрэк 2", 2004);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
        }

        [Fact]
        public async Task ShouldRejectAutomaticallyResolvedMovieWithSeriesType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = new Film
                {
                    KinopoiskId = 5273,
                    NameRu = "Шрэк 2",
                    NameOriginal = "Shrek 2",
                    Year = 2004,
                    Type = FilmType.TV_SERIES
                }
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = CreateMovieInfo("Шрэк 2", 2004);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
        }

        [Fact]
        public async Task ShouldAcceptAutomaticallyResolvedMiniSeries()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = new Film
                {
                    KinopoiskId = 12345,
                    NameRu = "Тестовый мини-сериал",
                    NameOriginal = "Test Mini Series",
                    Year = 2024,
                    Type = FilmType.MINI_SERIES
                }
            };
            var provider = CreateSeriesProvider(apiClient, 12345);
            var info = new SeriesInfo
            {
                Name = "Test Mini Series",
                Year = 2024
            };

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.NotNull(result.Item);
            Assert.Equal("12345", GetProviderId(info, Constants.ProviderId));
            Assert.Equal("12345", GetProviderId(result.Item, Constants.ProviderId));
        }

        [Fact]
        public async Task ShouldRejectAutomaticallyResolvedSeriesWithMovieType()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(12345, null, "Тестовый сериал", "Test Series", 2024)
            };
            var provider = CreateSeriesProvider(apiClient, 12345);
            var info = new SeriesInfo
            {
                Name = "Test Series",
                Year = 2024
            };

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.False(info.TryGetProviderId(Constants.ProviderId, out _));
        }

        [Fact]
        public async Task ShouldTrustKinopoiskIdFromPathWithoutTitleAndYearValidation()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(5273, null, "Шрэк 2", "Shrek 2", 2004)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = new MovieInfo
            {
                Name = "Пользовательское название",
                Path = "/media/movies/custom [kp-5273]/movie.mkv",
                Year = 1999
            };

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.NotNull(result.Item);
            Assert.Equal("5273", GetProviderId(info, Constants.ProviderId));
            Assert.Equal(1, apiClient.GetStaffCalls);
            Assert.Equal(1, apiClient.GetTrailersCalls);
        }

        [Fact]
        public async Task ShouldTrustStoredKinopoiskIdWithoutTitleAndYearValidation()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = CreateMovieFilm(5273, null, "Шрэк 2", "Shrek 2", 2004)
            };
            var provider = CreateMovieProvider(apiClient, 5273);
            var info = new MovieInfo
            {
                Name = "Пользовательское название",
                Year = 1999
            };
            info.SetProviderId(Constants.ProviderId, "5273");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.NotNull(result.Item);
            Assert.Equal("5273", GetProviderId(info, Constants.ProviderId));
        }

        private static MovieInfo CreateMovieInfo(
            string name,
            int year,
            string imdbId = null)
        {
            var info = new MovieInfo
            {
                Name = name,
                Year = year
            };

            if (!string.IsNullOrWhiteSpace(imdbId))
                info.SetProviderId(MetadataProvider.Imdb, imdbId);

            return info;
        }

        private static Film CreateMovieFilm(
            int kinopoiskId,
            string imdbId,
            string nameRu,
            string nameOriginal,
            int year)
        {
            return new Film
            {
                KinopoiskId = kinopoiskId,
                ImdbId = imdbId,
                NameRu = nameRu,
                NameOriginal = nameOriginal,
                Year = year,
                Type = FilmType.FILM
            };
        }

        private static MovieMetadataProvider CreateMovieProvider(
            IKinopoiskApiClient apiClient,
            int kinopoiskId)
        {
            return new MovieMetadataProvider(
                apiClient,
                new FixedProviderIdResolver<MovieInfo>(kinopoiskId),
                NullLogger<MovieMetadataProvider>.Instance,
                new FakeHttpClientFactory());
        }

        private static SeriesMetadataProvider CreateSeriesProvider(
            IKinopoiskApiClient apiClient,
            int kinopoiskId)
        {
            return new SeriesMetadataProvider(
                apiClient,
                new FixedProviderIdResolver<SeriesInfo>(kinopoiskId),
                NullLogger<SeriesMetadataProvider>.Instance,
                new FakeHttpClientFactory());
        }

        private static string GetProviderId(IHasProviderIds source, string providerName)
        {
            Assert.True(source.TryGetProviderId(providerName, out var providerId));
            return providerId;
        }

        private sealed class FixedProviderIdResolver<TLookupInfo> : IProviderIdResolver<TLookupInfo>
        {
            private readonly int _kinopoiskId;

            public FixedProviderIdResolver(int kinopoiskId)
            {
                _kinopoiskId = kinopoiskId;
            }

            public Task<(bool IsSuccess, int ProviderId)> TryResolve(
                TLookupInfo info,
                CancellationToken? ct = null)
            {
                return Task.FromResult((true, _kinopoiskId));
            }
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new HttpClient();
            }
        }

        private sealed class FakeKinopoiskApiClient : IKinopoiskApiClient
        {
            public Film Film { get; set; }

            public int GetSingleFilmCalls { get; private set; }

            public int GetStaffCalls { get; private set; }

            public int GetTrailersCalls { get; private set; }

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
                GetSingleFilmCalls++;
                return Task.FromResult(Film);
            }

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetStaffCalls++;
                return Task.FromResult<ICollection<StaffResponse>>(
                    Array.Empty<StaffResponse>());
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetTrailersCalls++;
                return Task.FromResult<VideoResponse>(null);
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }
        }
    }
}
