using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kinopoisk.Api;
using Jellyfin.Plugin.Kinopoisk.Playback;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskNativeTrailerBridgeTests
    {
        [Fact]
        public void ShouldAllowEveryPositiveKinopoiskId()
        {
            Assert.True(KinopoiskNativeTrailerBridge.IsAllowed(6638363));
            Assert.True(KinopoiskNativeTrailerBridge.IsAllowed(301));
            Assert.False(KinopoiskNativeTrailerBridge.IsAllowed(0));
            Assert.False(KinopoiskNativeTrailerBridge.IsAllowed(-1));
        }

        [Fact]
        public void ShouldRecognizeOnlyMarkedVirtualLocalTrailer()
        {
            var marked = new Trailer
            {
                ExtraType = ExtraType.Trailer,
                IsVirtualItem = true
            };
            marked.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "6638363";

            Assert.True(KinopoiskNativeTrailerBridge.TryGetKinopoiskId(marked, out var id));
            Assert.Equal(6638363, id);

            marked.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "301";
            Assert.True(KinopoiskNativeTrailerBridge.TryGetKinopoiskId(marked, out var secondId));
            Assert.Equal(301, secondId);

            marked.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "6638363";
            marked.ExtraType = ExtraType.Clip;
            Assert.False(KinopoiskNativeTrailerBridge.TryGetKinopoiskId(marked, out _));
        }

        [Fact]
        public void ShouldCreateStableTrailerIdPerMovie()
        {
            var movieId = Guid.Parse("19e6730c-cb15-4ed2-8f5b-278109a83bb3");

            var first = KinopoiskNativeTrailerBridge.CreateTrailerId(movieId, 6638363);
            var second = KinopoiskNativeTrailerBridge.CreateTrailerId(movieId, 6638363);
            var anotherMovie = KinopoiskNativeTrailerBridge.CreateTrailerId(Guid.NewGuid(), 6638363);

            Assert.Equal(first, second);
            Assert.NotEqual(Guid.Empty, first);
            Assert.NotEqual(first, anotherMovie);
        }

        [Fact]
        public void ShouldCreateAndroidTvPlayableTrailerWithFilteredPlaceholder()
        {
            var movie = new Movie
            {
                Id = Guid.Parse("e32d6359-2358-b57c-d0a7-35c81eeba24e")
            };

            var trailer = KinopoiskNativeTrailerBridge.CreateTrailer(
                movie,
                6638363,
                "Трейлер фильма «Астронавт»");

            Assert.IsType<KinopoiskNativeTrailerItem>(trailer);
            Assert.Equal(LocationType.Remote, trailer.LocationType);
            Assert.False(trailer.IsVirtualItem);
            Assert.Equal(BaseItemKind.Trailer, trailer.GetBaseItemKind());
            var staticSources = Assert.IsAssignableFrom<
                IEnumerable<(BaseItem Item, MediaSourceType MediaSourceType)>>(
                typeof(KinopoiskNativeTrailerItem)
                    .GetMethod(
                        "GetAllItemsForMediaSources",
                        BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(trailer, null));
            var placeholder = Assert.Single(staticSources);
            Assert.Same(trailer, placeholder.Item);
            Assert.Equal(MediaSourceType.Placeholder, placeholder.MediaSourceType);
            Assert.True(KinopoiskNativeTrailerBridge.IsAndroidTvPlayable(trailer));
            Assert.NotEqual(
                KinopoiskNativeTrailerBridge.CreateTrailerId(movie.Id, 6638363),
                trailer.Id);
        }

        [Fact]
        public void ShouldRejectLegacyVirtualTrailerForAndroidTvPlayback()
        {
            var legacy = new Trailer
            {
                IsVirtualItem = true
            };

            Assert.False(KinopoiskNativeTrailerBridge.IsAndroidTvPlayable(legacy));
        }

        [Fact]
        public void ShouldMatchOnlyExplicitlySelectedMovie()
        {
            var selectedId = Guid.Parse("e32d6359-2358-b57c-d0a7-35c81eeba24e");
            var movie = new Movie { Id = selectedId };
            movie.ProviderIds["kinopoisk"] = "6638363";

            Assert.True(KinopoiskNativeTrailerBridge.MatchesSelection(
                movie,
                6638363,
                selectedId));
            Assert.False(KinopoiskNativeTrailerBridge.MatchesSelection(
                movie,
                6638363,
                Guid.Parse("3910ac1a-5a57-06f2-0324-f3845c48aecf")));
            Assert.False(KinopoiskNativeTrailerBridge.MatchesSelection(
                movie,
                430,
                selectedId));
        }

        [Fact]
        public async Task ShouldExposeServerRemuxedHlsWithRequiredHeadersAndAudio()
        {
            var playback = new FakePlaybackService
            {
                Response = new KinopoiskTrailerPlaybackResponse
                {
                    KinopoiskId = 6638363,
                    Selected = new KinopoiskTrailerPlaybackSource
                    {
                        Provider = "kinopoisk",
                        PlaybackKind = "hls",
                        Url = "https://strm.yandex.ru/trailer/master.m3u8",
                        RequestHeaders = new Dictionary<string, string>
                        {
                            ["Referer"] = "https://widgets.kinopoisk.ru/discovery/film/6638363/trailer/1",
                            ["Origin"] = "https://widgets.kinopoisk.ru"
                        }
                    }
                }
            };
            var provider = new KinopoiskNativeTrailerMediaSourceProvider(
                playback,
                new FakeNativeTrailerCache());
            var trailer = new Trailer
            {
                Id = Guid.Parse("6cab727b-fba5-6868-c53d-bddd7aa8ecc5"),
                ExtraType = ExtraType.Trailer,
                IsVirtualItem = true
            };
            trailer.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "6638363";

            var source = Assert.Single(await provider.GetMediaSources(
                trailer,
                CancellationToken.None));

            Assert.Equal("6cab727bfba56868c53dbddd7aa8ecc5", source.Id);
            Assert.Equal("https://strm.yandex.ru/trailer/master.m3u8", source.Path);
            Assert.Equal(MediaProtocol.File, source.Protocol);
            Assert.False(source.IsRemote);
            Assert.Equal("https://strm.yandex.ru/trailer/master.m3u8", source.EncoderPath);
            Assert.Equal(MediaProtocol.Http, source.EncoderProtocol);
            Assert.False(source.SupportsDirectPlay);
            Assert.False(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
            Assert.Null(source.AnalyzeDurationMs);
            Assert.Equal("https://widgets.kinopoisk.ru", source.RequiredHttpHeaders["Origin"]);
            Assert.Contains(source.MediaStreams, stream =>
                stream.Type == MediaStreamType.Video
                && string.Equals(stream.Codec, "h264", StringComparison.Ordinal));
            Assert.Contains(source.MediaStreams, stream =>
                stream.Type == MediaStreamType.Audio
                && string.Equals(stream.Codec, "aac", StringComparison.Ordinal)
                && stream.Channels == 2);
        }

        [Fact]
        public async Task ShouldIgnoreRegularTrailersAndYoutubeFallback()
        {
            var playback = new FakePlaybackService
            {
                Response = new KinopoiskTrailerPlaybackResponse
                {
                    Selected = new KinopoiskTrailerPlaybackSource
                    {
                        Provider = "youtube",
                        PlaybackKind = "youtube",
                        Url = "https://www.youtube.com/watch?v=video"
                    }
                }
            };
            var provider = new KinopoiskNativeTrailerMediaSourceProvider(
                playback,
                new FakeNativeTrailerCache());

            Assert.Empty(await provider.GetMediaSources(
                new Trailer { ExtraType = ExtraType.Trailer },
                CancellationToken.None));

            var marked = new Trailer { ExtraType = ExtraType.Trailer };
            marked.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "6638363";
            Assert.Empty(await provider.GetMediaSources(marked, CancellationToken.None));
        }

        [Fact]
        public void ShouldProtectBridgeControllerWithAdministratorPolicy()
        {
            var authorize = typeof(KinopoiskNativeTrailerBridgeController)
                .GetCustomAttribute<AuthorizeAttribute>();

            Assert.NotNull(authorize);
            Assert.Equal(Policies.RequiresElevation, authorize!.Policy);
        }

        [Fact]
        public void ShouldAcceptExplicitMovieIdForEveryBridgeOperation()
        {
            foreach (var methodName in new[] { "GetStatus", "Prepare", "Remove" })
            {
                var method = typeof(KinopoiskNativeTrailerBridgeController)
                    .GetMethod(methodName);

                Assert.NotNull(method);
                Assert.Contains(method!.GetParameters(), parameter =>
                    parameter.Name == "movieId"
                    && parameter.ParameterType == typeof(Guid?));
            }
        }

        [Fact]
        public void ShouldAdvertiseNativeBridgeCapabilities()
        {
            var capabilities = new KinopoiskPlaybackCapabilities();

            Assert.False(capabilities.NativeTrailerBridge.Experimental);
            Assert.Empty(capabilities.NativeTrailerBridge.AllowedKinopoiskIds);
            Assert.True(capabilities.NativeTrailerBridge.AllKinopoiskMovies);
            Assert.True(capabilities.NativeTrailerBridge.AutomaticLocalTrailerRegistration);
            Assert.True(capabilities.NativeTrailerBridge.LazyCardWarmup);
            Assert.Equal("OnDemand", capabilities.NativeTrailerBridge.CachePopulationMode);
            Assert.Equal("AllClients", capabilities.NativeTrailerBridge.ClientScope);
            Assert.Equal(25, capabilities.NativeTrailerBridge.PlaybackStartupWaitSeconds);
            Assert.False(capabilities.NativeTrailerBridge.VirtualLocalTrailer);
            Assert.True(capabilities.NativeTrailerBridge.AndroidTvRemoteLocation);
            Assert.True(capabilities.NativeTrailerBridge.DynamicMediaSourceOnly);
            Assert.True(capabilities.NativeTrailerBridge.ServerSideMediaHeaders);
            Assert.True(capabilities.NativeTrailerBridge.ExplicitMovieSelection);
            Assert.True(capabilities.NativeTrailerBridge.StableMediaSourceId);
            Assert.True(capabilities.NativeTrailerBridge.AndroidTvFileProtocol);
            Assert.True(capabilities.NativeTrailerBridge.SanitizedHlsManifestUrl);
            Assert.False(capabilities.NativeTrailerBridge.ServerSideHlsRemux);
            Assert.Equal(0, capabilities.NativeTrailerBridge.HlsAnalyzeDurationMs);
            Assert.True(capabilities.NativeTrailerBridge.LocalTrailerCache);
            Assert.Equal(
                4L * 1024L * 1024L * 1024L,
                capabilities.NativeTrailerBridge.LocalTrailerCacheMaximumBytes);
            Assert.Equal(30, capabilities.NativeTrailerBridge.LocalTrailerCacheRetentionDays);
            Assert.True(capabilities.NativeTrailerBridge.LocalMp4RemuxWithoutReencoding);
            Assert.True(capabilities.NativeTrailerBridge.NativeLocalFileDirectPlay);
            Assert.True(capabilities.NativeTrailerBridge.WeeklyLruCleanup);
            Assert.True(capabilities.NativeTrailerBridge.R7ServerTranscodeFallback);
        }

        [Fact]
        public void ShouldSelectOnlyHttpsKinopoiskWidgetForLocalCache()
        {
            var trailers = new MediaUrl[]
            {
                new() { Name = "YouTube", Url = "https://www.youtube.com/watch?v=abcdefghijk" },
                new() { Name = "КиноПоиск", Url = "https://widgets.kinopoisk.ru/discovery/trailer/207656" }
            };

            Assert.True(KinopoiskNativeTrailerBridge.TrySelectKinopoiskWidget(
                trailers,
                out var selected));
            Assert.Equal("КиноПоиск", selected.Name);
            Assert.False(KinopoiskNativeTrailerBridge.TrySelectKinopoiskWidget(
                new[] { trailers[0] },
                out _));
        }

        [Theory]
        [InlineData("/Items/e32d63592358b57cd0a735c81eeba24e")]
        [InlineData("/Items/e32d63592358b57cd0a735c81eeba24e/LocalTrailers")]
        [InlineData("/Users/11111111111111111111111111111111/Items/e32d63592358b57cd0a735c81eeba24e")]
        public void ShouldRecognizeStandardMovieCardRoutes(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;

            Assert.True(KinopoiskNativeTrailerLazyPrepareMiddleware.ShouldPrepare(
                context.Request,
                out var movieId));
            Assert.Equal(
                Guid.Parse("e32d6359-2358-b57c-d0a7-35c81eeba24e"),
                movieId);
        }

        [Fact]
        public void ShouldRespectConfiguredClientScope()
        {
            Assert.True(KinopoiskNativeTrailerLazyPrepareMiddleware.IsClientAllowed(
                "Jellyfin Web",
                KinopoiskTrailerCacheClientScope.AllClients));
            Assert.True(KinopoiskNativeTrailerLazyPrepareMiddleware.IsClientAllowed(
                "Jellyfin Android TV",
                KinopoiskTrailerCacheClientScope.AndroidTvOnly));
            Assert.False(KinopoiskNativeTrailerLazyPrepareMiddleware.IsClientAllowed(
                "Jellyfin Web",
                KinopoiskTrailerCacheClientScope.AndroidTvOnly));
        }

        [Fact]
        public async Task ShouldPreferCachedMp4ForNativeDirectPlay()
        {
            var cache = new FakeNativeTrailerCache
            {
                Entry = new KinopoiskNativeTrailerCacheEntry
                {
                    KinopoiskId = 6638363,
                    Path = "/cache/trailers/kinopoisk-6638363.mp4",
                    ContentLength = 12_345_678,
                    Container = "mp4",
                    VideoCodec = "h264",
                    VideoProfile = "Main",
                    Width = 640,
                    Height = 480,
                    FrameRate = 25,
                    AudioCodec = "aac",
                    AudioChannels = 2,
                    AudioSampleRate = 48000,
                    RunTimeTicks = TimeSpan.FromSeconds(90).Ticks
                }
            };
            var provider = new KinopoiskNativeTrailerMediaSourceProvider(
                new FakePlaybackService(),
                cache);
            var trailer = new Trailer
            {
                Id = Guid.Parse("6cab727b-fba5-6868-c53d-bddd7aa8ecc5"),
                ExtraType = ExtraType.Trailer
            };
            trailer.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = "6638363";

            var source = Assert.Single(await provider.GetMediaSources(
                trailer,
                CancellationToken.None));

            Assert.Equal(MediaProtocol.File, source.Protocol);
            Assert.Equal("mp4", source.Container);
            Assert.False(source.IsRemote);
            Assert.True(source.SupportsDirectPlay);
            Assert.True(source.SupportsDirectStream);
            Assert.True(source.SupportsTranscoding);
            Assert.False(source.SupportsProbing);
            Assert.Equal(12_345_678, source.Size);
            Assert.Equal(TimeSpan.FromSeconds(90).Ticks, source.RunTimeTicks);
            Assert.Contains(source.MediaStreams, stream =>
                stream.Type == MediaStreamType.Video
                && stream.Codec == "h264"
                && stream.Width == 640
                && stream.Height == 480);
            Assert.Contains(source.MediaStreams, stream =>
                stream.Type == MediaStreamType.Audio
                && stream.Codec == "aac"
                && stream.Channels == 2);
        }

        private sealed class FakePlaybackService : IKinopoiskTrailerPlaybackService
        {
            public KinopoiskTrailerPlaybackResponse Response { get; set; }

            public Task<KinopoiskTrailerPlaybackResponse> Get(
                int kinopoiskId,
                CancellationToken cancellationToken)
                => Task.FromResult(Response);
        }

        private sealed class FakeNativeTrailerCache : IKinopoiskNativeTrailerCache
        {
            public bool Enabled => true;

            public KinopoiskNativeTrailerCacheEntry Entry { get; set; }

            public string GetExpectedPath(int kinopoiskId) => Entry?.Path ?? string.Empty;

            public KinopoiskNativeTrailerCacheEntry TryGet(int kinopoiskId, bool touch = true)
                => Entry;

            public Task<KinopoiskNativeTrailerCacheEntry> GetOrCreate(
                int kinopoiskId,
                KinopoiskTrailerPlaybackSource source,
                CancellationToken cancellationToken)
                => Task.FromResult(Entry);

            public Task<KinopoiskNativeTrailerCacheCleanupResult> Cleanup(
                CancellationToken cancellationToken)
                => Task.FromResult(new KinopoiskNativeTrailerCacheCleanupResult());

            public KinopoiskNativeTrailerCacheSnapshot GetSnapshot()
                => new()
                {
                    Enabled = true,
                    MaximumBytes = 4L * 1024L * 1024L * 1024L,
                    RetentionDays = 30,
                    CurrentBytes = Entry?.ContentLength ?? 0,
                    FileCount = Entry is null ? 0 : 1
                };
        }
    }
}
