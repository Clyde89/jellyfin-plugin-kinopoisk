#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Dto;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.MediaInfo;
using Microsoft.Extensions.DependencyInjection;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Выдаёт локальный MP4 из кэша либо проверенный HLS-резерв.
    /// </summary>
    public sealed class KinopoiskNativeTrailerMediaSourceProvider : IMediaSourceProvider
    {
        private readonly IKinopoiskTrailerPlaybackService _playbackService;
        private readonly IKinopoiskNativeTrailerCache _cache;
        private readonly IKinopoiskNativeTrailerCacheWarmupService? _warmupService;
        private readonly KinopoiskNativeTrailerCacheOptions _options;

        public KinopoiskNativeTrailerMediaSourceProvider(
            IKinopoiskTrailerPlaybackService playbackService,
            IKinopoiskNativeTrailerCache cache)
            : this(
                playbackService,
                cache,
                null,
                new KinopoiskNativeTrailerCacheOptions())
        {
        }

        [ActivatorUtilitiesConstructor]
        public KinopoiskNativeTrailerMediaSourceProvider(
            IKinopoiskTrailerPlaybackService playbackService,
            IKinopoiskNativeTrailerCache cache,
            IKinopoiskNativeTrailerCacheWarmupService? warmupService,
            KinopoiskNativeTrailerCacheOptions options)
        {
            _playbackService = playbackService
                ?? throw new ArgumentNullException(nameof(playbackService));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _warmupService = warmupService;
            _options = options ?? throw new ArgumentNullException(nameof(options));
        }

        public async Task<IEnumerable<MediaSourceInfo>> GetMediaSources(
            BaseItem item,
            CancellationToken cancellationToken)
        {
            if (!KinopoiskNativeTrailerBridge.TryGetKinopoiskId(
                    item,
                    out var kinopoiskId))
            {
                return Array.Empty<MediaSourceInfo>();
            }

            var cached = _cache.TryGet(kinopoiskId);
            if (cached is not null)
                return new[] { CreateCachedSource(item, cached) };

            if (_warmupService is not null)
            {
                cached = await _warmupService
                    .WaitForReady(
                        kinopoiskId,
                        _options.PlaybackStartupWait,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (cached is not null)
                    return new[] { CreateCachedSource(item, cached) };
            }

            var playback = await _playbackService
                .Get(kinopoiskId, cancellationToken)
                .ConfigureAwait(false);
            if (playback is null
                || !string.Equals(
                    playback.Selected.Provider,
                    "kinopoisk",
                    StringComparison.OrdinalIgnoreCase)
                || !string.Equals(
                    playback.Selected.PlaybackKind,
                    "hls",
                    StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(
                    playback.Selected.Url,
                    UriKind.Absolute,
                    out var mediaUri)
                || mediaUri.Scheme != Uri.UriSchemeHttps)
            {
                return Array.Empty<MediaSourceInfo>();
            }

            return new[] { CreateRemoteFallback(item, mediaUri, playback.Selected) };
        }

        private static MediaSourceInfo CreateCachedSource(
            BaseItem item,
            KinopoiskNativeTrailerCacheEntry entry)
            => new()
            {
                Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                Name = "КиноПоиск — локальный трейлер",
                Path = entry.Path,
                EncoderPath = entry.Path,
                Protocol = MediaProtocol.File,
                EncoderProtocol = MediaProtocol.File,
                Type = MediaSourceType.Default,
                Container = entry.Container,
                IsRemote = false,
                SupportsDirectPlay = true,
                SupportsDirectStream = true,
                SupportsTranscoding = true,
                SupportsProbing = false,
                Size = entry.ContentLength,
                RunTimeTicks = entry.RunTimeTicks,
                Bitrate = entry.TotalBitRate,
                MediaStreams = new MediaStream[]
                {
                    new()
                    {
                        Index = 0,
                        Type = MediaStreamType.Video,
                        Codec = entry.VideoCodec,
                        Profile = entry.VideoProfile,
                        Level = entry.VideoLevel,
                        BitRate = entry.VideoBitRate,
                        Width = entry.Width,
                        Height = entry.Height,
                        AverageFrameRate = entry.FrameRate,
                        BitDepth = entry.VideoBitDepth,
                        PixelFormat = entry.PixelFormat,
                        IsDefault = true
                    },
                    new()
                    {
                        Index = 1,
                        Type = MediaStreamType.Audio,
                        Codec = entry.AudioCodec,
                        BitRate = entry.AudioBitRate,
                        Channels = entry.AudioChannels,
                        SampleRate = entry.AudioSampleRate,
                        IsDefault = true
                    }
                }
            };

        private static MediaSourceInfo CreateRemoteFallback(
            BaseItem item,
            Uri mediaUri,
            Jellyfin.Plugin.Kinopoisk.Playback.KinopoiskTrailerPlaybackSource source)
            => new()
            {
                    // LocalTrailers содержит служебный Placeholder с ID элемента.
                    // Android TV закрепляет этот ID в PlaybackInfo, поэтому
                    // динамический источник обязан использовать то же значение.
                    Id = item.Id.ToString("N", CultureInfo.InvariantCulture),
                    Name = "КиноПоиск NativeTrailerBridge",
                    Path = mediaUri.AbsoluteUri,
                    // Клиент видит серверный источник как File/non-remote и всегда
                    // использует выданный Jellyfin transcodingUrl. FFmpeg при этом
                    // получает настоящий HTTPS-адрес через EncoderPath/Protocol.
                    Protocol = MediaProtocol.File,
                    EncoderPath = mediaUri.AbsoluteUri,
                    EncoderProtocol = MediaProtocol.Http,
                    Type = MediaSourceType.Default,
                    Container = "hls",
                    IsRemote = false,
                    SupportsDirectPlay = false,
                    // Jellyfin 10.11 принудительно выключает direct stream для
                    // обычного HTTP-входа. Сохраняем воспроизводимый путь r7 как
                    // автоматический резерв, если локального MP4 ещё нет.
                    SupportsDirectStream = false,
                    SupportsTranscoding = true,
                    SupportsProbing = true,
                    RequiredHttpHeaders = source.RequestHeaders
                        .ToDictionary(
                            pair => pair.Key,
                            pair => pair.Value,
                            StringComparer.OrdinalIgnoreCase),
                    MediaStreams = new MediaStream[]
                    {
                        new()
                        {
                            Index = 0,
                            Type = MediaStreamType.Video,
                            Codec = "h264",
                            IsDefault = true
                        },
                        new()
                        {
                            Index = 1,
                            Type = MediaStreamType.Audio,
                            Codec = "aac",
                            Channels = 2,
                            IsDefault = true
                        }
                    }
                };

        public Task<ILiveStream> OpenMediaSource(
            string openToken,
            List<ILiveStream> currentLiveStreams,
            CancellationToken cancellationToken)
            => Task.FromException<ILiveStream>(
                new NotSupportedException(
                    "NativeTrailerBridge не использует открываемые live-потоки."));
    }
}
