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
    public class MovieMetadataProviderTests
    {
        [Fact]
        public async Task ShouldKeepResolvedMetadataWhenSupplementalRequestsFail()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = new Film
                {
                    KinopoiskId = 5273,
                    ImdbId = "tt0298148",
                    NameRu = "Шрэк 2",
                    NameOriginal = "Shrek 2",
                    Year = 2004
                },
                StaffException = new InvalidOperationException("staff failed"),
                TrailersException = new InvalidOperationException("trailers failed")
            };
            var resolver = new FixedProviderIdResolver(5273);
            var provider = CreateProvider(apiClient, resolver);
            var info = new MovieInfo
            {
                Name = "Шрэк 2",
                Year = 2004
            };
            info.SetProviderId("Imdb", "tt0298148");
            info.SetProviderId("Tmdb", "809");

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.NotNull(result.Item);
            Assert.Equal("5273", GetProviderId(info, Constants.ProviderId));
            Assert.Equal("5273", GetProviderId(result.Item, Constants.ProviderId));
            Assert.Equal("tt0298148", GetProviderId(result.Item, "Imdb"));
            Assert.Equal("809", GetProviderId(result.Item, "Tmdb"));
            Assert.Equal(1, apiClient.GetSingleFilmCalls);
            Assert.Equal(1, apiClient.GetStaffCalls);
            Assert.Equal(1, apiClient.GetTrailersCalls);
        }

        [Fact]
        public async Task ShouldPropagateCancellationFromSupplementalRequest()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Film = new Film
                {
                    KinopoiskId = 5273,
                    NameRu = "Шрэк 2",
                    Year = 2004
                },
                StaffException = new OperationCanceledException()
            };
            var provider = CreateProvider(apiClient, new FixedProviderIdResolver(5273));
            var info = new MovieInfo
            {
                Name = "Шрэк 2",
                Year = 2004
            };

            await Assert.ThrowsAsync<OperationCanceledException>(
                () => provider.GetMetadata(info, CancellationToken.None));
        }

        private static MovieMetadataProvider CreateProvider(
            IKinopoiskApiClient apiClient,
            IProviderIdResolver<MovieInfo> resolver)
        {
            return new MovieMetadataProvider(
                apiClient,
                resolver,
                NullLogger<MovieMetadataProvider>.Instance,
                new FakeHttpClientFactory());
        }

        private static string GetProviderId(IHasProviderIds source, string providerName)
        {
            Assert.True(source.TryGetProviderId(providerName, out var providerId));
            return providerId;
        }

        private sealed class FixedProviderIdResolver : IProviderIdResolver<MovieInfo>
        {
            private readonly int _providerId;

            public FixedProviderIdResolver(int providerId)
            {
                _providerId = providerId;
            }

            public Task<(bool IsSuccess, int ProviderId)> TryResolve(
                MovieInfo info,
                CancellationToken? ct = null)
            {
                return Task.FromResult((true, _providerId));
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

            public Exception StaffException { get; set; }

            public Exception TrailersException { get; set; }

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

                if (StaffException is not null)
                    return Task.FromException<ICollection<StaffResponse>>(StaffException);

                return Task.FromResult<ICollection<StaffResponse>>(
                    Array.Empty<StaffResponse>());
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetTrailersCalls++;

                if (TrailersException is not null)
                    return Task.FromException<VideoResponse>(TrailersException);

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
