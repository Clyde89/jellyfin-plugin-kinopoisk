using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kinopoisk.Playback
{
    /// <summary>
    /// Описывает возможности серверного API воспроизведения трейлеров.
    /// </summary>
    public sealed class KinopoiskPlaybackCapabilities
    {
        public int SchemaVersion { get; set; } = 1;

        public IReadOnlyList<string> SourcePriority { get; set; }
            = new[] { "kinopoisk", "youtube" };

        public IReadOnlyList<string> PlaybackKinds { get; set; }
            = new[] { "hls", "youtube" };

        public bool DirectHls { get; set; } = true;

        public bool HlsProxy { get; set; }

        public bool YoutubeFallback { get; set; } = true;

        public string YoutubePlaybackRoute { get; set; } = "client-direct";

        public bool YoutubeServerProxy { get; set; }

        public KinopoiskNativeTrailerBridgeCapabilities NativeTrailerBridge { get; set; }
            = new();
    }

    /// <summary>
    /// Описывает единый серверный механизм трейлеров для клиентов Jellyfin.
    /// </summary>
    public sealed class KinopoiskNativeTrailerBridgeCapabilities
    {
        public bool Experimental { get; set; }

        public IReadOnlyList<int> AllowedKinopoiskIds { get; set; }
            = Array.Empty<int>();

        public bool AllKinopoiskMovies { get; set; } = true;

        public bool AutomaticLocalTrailerRegistration { get; set; } = true;

        public bool LazyCardWarmup { get; set; } = true;

        public string CachePopulationMode { get; set; } = "OnDemand";

        public string ClientScope { get; set; } = "AllClients";

        public int PlaybackStartupWaitSeconds { get; set; } = 25;

        public bool VirtualLocalTrailer { get; set; }

        public bool AndroidTvRemoteLocation { get; set; } = true;

        public bool DynamicMediaSourceOnly { get; set; } = true;

        public bool ServerSideMediaHeaders { get; set; } = true;

        public bool ExplicitMovieSelection { get; set; } = true;

        public bool StableMediaSourceId { get; set; } = true;

        public bool AndroidTvFileProtocol { get; set; } = true;

        public bool SanitizedHlsManifestUrl { get; set; } = true;

        public bool ServerSideHlsRemux { get; set; }

        public int HlsAnalyzeDurationMs { get; set; }

        public bool LocalTrailerCache { get; set; } = true;

        public long LocalTrailerCacheMaximumBytes { get; set; }
            = 4L * 1024L * 1024L * 1024L;

        public int LocalTrailerCacheRetentionDays { get; set; } = 30;

        public bool LocalMp4RemuxWithoutReencoding { get; set; } = true;

        public bool NativeLocalFileDirectPlay { get; set; } = true;

        public bool WeeklyLruCleanup { get; set; } = true;

        public bool R7ServerTranscodeFallback { get; set; } = true;
    }

    /// <summary>
    /// Содержит выбранный сервером трейлер и резервные варианты.
    /// </summary>
    public sealed class KinopoiskTrailerPlaybackResponse
    {
        public int SchemaVersion { get; set; } = 1;

        public int KinopoiskId { get; set; }

        public KinopoiskTrailerPlaybackSource Selected { get; set; } = new();

        public IReadOnlyList<KinopoiskTrailerPlaybackSource> Fallbacks { get; set; }
            = Array.Empty<KinopoiskTrailerPlaybackSource>();
    }

    /// <summary>
    /// Описывает один способ воспроизведения трейлера.
    /// </summary>
    public sealed class KinopoiskTrailerPlaybackSource
    {
        public string Provider { get; set; } = string.Empty;

        public string PlaybackKind { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string Url { get; set; } = string.Empty;

        public string VideoId { get; set; } = string.Empty;

        public string ContentType { get; set; } = string.Empty;

        public IReadOnlyDictionary<string, string> RequestHeaders { get; set; }
            = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }
}
