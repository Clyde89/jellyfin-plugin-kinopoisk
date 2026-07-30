using System;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using ApiEpisode = KinopoiskUnofficialInfo.ApiClient.Episode;
using ApiSeason = KinopoiskUnofficialInfo.ApiClient.Season;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class SeasonEpisodeMetadataProviderTests
    {
        [Fact]
        public async Task ShouldFillSeasonMetadataFromEpisodes()
        {
            var provider = new SeasonMetadataProvider(
                new FixedSeasonApiClient(CreateResponse()),
                NullLogger<SeasonMetadataProvider>.Instance,
                new FakeHttpClientFactory());
            var info = new SeasonInfo { IndexNumber = 1 };
            info.SeriesProviderIds[Constants.ProviderId] = "1046206";

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Сезон 1", result.Item.Name);
            Assert.Equal(1, result.Item.IndexNumber);
            Assert.Equal(2019, result.Item.ProductionYear);
            Assert.Equal(2, result.Item.PremiereDate?.Day);
            Assert.Contains("2 эпизодов", result.Item.Overview);
        }

        [Fact]
        public async Task ShouldFillSingleEpisodeMetadata()
        {
            var provider = new EpisodeMetadataProvider(
                new FixedSeasonApiClient(CreateResponse()),
                NullLogger<EpisodeMetadataProvider>.Instance,
                new FakeHttpClientFactory());
            var info = CreateEpisodeInfo(1, 2);

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Кот в мешке", result.Item.Name);
            Assert.Equal("Cat's in the Bag...", result.Item.OriginalTitle);
            Assert.Equal("Описание второй серии", result.Item.Overview);
            Assert.Equal(1, result.Item.ParentIndexNumber);
            Assert.Equal(2, result.Item.IndexNumber);
            Assert.Equal(2019, result.Item.ProductionYear);
        }

        [Fact]
        public async Task ShouldCombineMultipartEpisodeMetadata()
        {
            var provider = new EpisodeMetadataProvider(
                new FixedSeasonApiClient(CreateResponse()),
                NullLogger<EpisodeMetadataProvider>.Instance,
                new FakeHttpClientFactory());
            var info = CreateEpisodeInfo(1, 1);
            info.IndexNumberEnd = 2;

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.Equal("Пилот / Кот в мешке", result.Item.Name);
            Assert.Equal("Pilot / Cat's in the Bag...", result.Item.OriginalTitle);
            Assert.Contains("Описание первой серии", result.Item.Overview);
            Assert.Contains("Описание второй серии", result.Item.Overview);
            Assert.Equal(2, result.Item.IndexNumberEnd);
            Assert.Equal(2, result.Item.PremiereDate?.Day);
        }

        [Fact]
        public async Task ShouldSkipMissingEpisode()
        {
            var apiClient = new CountingSeasonApiClient(CreateResponse());
            var provider = new EpisodeMetadataProvider(
                apiClient,
                NullLogger<EpisodeMetadataProvider>.Instance,
                new FakeHttpClientFactory());
            var info = CreateEpisodeInfo(1, 1);
            info.IsMissingEpisode = true;

            var result = await provider.GetMetadata(info, CancellationToken.None);

            Assert.False(result.HasMetadata);
            Assert.Equal(0, apiClient.RequestCount);
        }

        private static EpisodeInfo CreateEpisodeInfo(int seasonNumber, int episodeNumber)
        {
            var info = new EpisodeInfo
            {
                ParentIndexNumber = seasonNumber,
                IndexNumber = episodeNumber
            };
            info.SeriesProviderIds[Constants.ProviderId] = "1046206";
            return info;
        }

        private static SeasonResponse CreateResponse()
        {
            return new SeasonResponse
            {
                Total = 1,
                Items = new[]
                {
                    new ApiSeason
                    {
                        Number = 1,
                        Episodes = new[]
                        {
                            new ApiEpisode
                            {
                                SeasonNumber = 1,
                                EpisodeNumber = 1,
                                NameRu = "Пилот",
                                NameEn = "Pilot",
                                Synopsis = "Описание первой серии",
                                ReleaseDate = "2019-01-02"
                            },
                            new ApiEpisode
                            {
                                SeasonNumber = 1,
                                EpisodeNumber = 2,
                                NameRu = "Кот в мешке",
                                NameEn = "Cat's in the Bag...",
                                Synopsis = "Описание второй серии",
                                ReleaseDate = "2019-01-09"
                            }
                        }
                    }
                }
            };
        }

        private sealed class FixedSeasonApiClient : IKinopoiskSeasonApiClient
        {
            private readonly SeasonResponse _response;

            public FixedSeasonApiClient(SeasonResponse response)
            {
                _response = response;
            }

            public Task<SeasonResponse> GetSeasons(
                int filmId,
                CancellationToken? cancellationToken = null)
                => Task.FromResult(_response);
        }

        private sealed class CountingSeasonApiClient : IKinopoiskSeasonApiClient
        {
            private readonly SeasonResponse _response;

            public CountingSeasonApiClient(SeasonResponse response)
            {
                _response = response;
            }

            public int RequestCount { get; private set; }

            public Task<SeasonResponse> GetSeasons(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RequestCount++;
                return Task.FromResult(_response);
            }
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new HttpClient();
            }
        }
    }
}
