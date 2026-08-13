using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTrailerPlaybackServiceTests
    {
        [Fact]
        public async Task ShouldSelectResolvedKinopoiskAndKeepYoutubeFallback()
        {
            var resolver = new FakeStreamResolver
            {
                Result = new KinopoiskResolvedTrailerStream
                {
                    WidgetUrl = new Uri(
                        "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1"),
                    MediaUrl = new Uri("https://strm.yandex.ru/trailer/master.m3u8")
                }
            };
            var service = CreateService(CreateTrailers(), resolver);

            var result = await service.Get(430, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("kinopoisk", result!.Selected.Provider);
            Assert.Equal("hls", result.Selected.PlaybackKind);
            Assert.Equal("https://strm.yandex.ru/trailer/master.m3u8", result.Selected.Url);
            var fallback = Assert.Single(result.Fallbacks);
            Assert.Equal("youtube", fallback.Provider);
            Assert.Equal("YOUTUBE001", fallback.VideoId);
        }

        [Fact]
        public async Task ShouldSelectYoutubeWhenKinopoiskCannotBeResolved()
        {
            var resolver = new FakeStreamResolver();
            var service = CreateService(CreateTrailers(), resolver);

            var result = await service.Get(430, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("youtube", result!.Selected.Provider);
            Assert.Equal("youtube", result.Selected.PlaybackKind);
            Assert.Equal("YOUTUBE001", result.Selected.VideoId);
        }

        [Fact]
        public async Task ShouldSelectYoutubeWhenKinopoiskResolverFails()
        {
            var resolver = new FakeStreamResolver
            {
                Exception = new HttpRequestException("widget unavailable")
            };
            var service = CreateService(CreateTrailers(), resolver);

            var result = await service.Get(430, CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal("youtube", result!.Selected.Provider);
        }

        [Fact]
        public async Task ShouldReturnNullWithoutPlayableSources()
        {
            var service = CreateService(
                new VideoResponse { Items = new Collection<VideoResponse_items>() },
                new FakeStreamResolver());

            Assert.Null(await service.Get(430, CancellationToken.None));
        }

        private static KinopoiskTrailerPlaybackService CreateService(
            VideoResponse trailers,
            IKinopoiskTrailerStreamResolver resolver)
            => new(
                new FakeApiClient(trailers),
                resolver,
                NullLogger<KinopoiskTrailerPlaybackService>.Instance);

        private static VideoResponse CreateTrailers()
            => new()
            {
                Items = new Collection<VideoResponse_items>
                {
                    new()
                    {
                        Name = "Трейлер",
                        Site = VideoResponse_itemsSite.KINOPOISK_WIDGET,
                        Url = "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1"
                    },
                    new()
                    {
                        Name = "Официальный дублированный трейлер",
                        Site = VideoResponse_itemsSite.YOUTUBE,
                        Url = "https://youtu.be/YOUTUBE001"
                    }
                }
            };

        private sealed class FakeStreamResolver : IKinopoiskTrailerStreamResolver
        {
            public KinopoiskResolvedTrailerStream Result { get; set; }

            public Exception Exception { get; set; }

            public Task<KinopoiskResolvedTrailerStream> Resolve(
                string widgetUrl,
                CancellationToken cancellationToken)
            {
                if (Exception is not null)
                    return Task.FromException<KinopoiskResolvedTrailerStream>(Exception);
                return Task.FromResult(Result);
            }
        }

        private sealed class FakeApiClient : IKinopoiskApiClient
        {
            private readonly VideoResponse _trailers;

            public FakeApiClient(VideoResponse trailers)
            {
                _trailers = trailers;
            }

            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
                => Task.FromResult(_trailers);

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();
        }
    }
}
