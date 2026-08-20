#nullable enable

using System;

namespace Jellyfin.Plugin.Kinopoisk.Playback
{
    /// <summary>
    /// Состояние серверного LocalTrailer КиноПоиска.
    /// </summary>
    public sealed class KinopoiskNativeTrailerBridgeResult
    {
        public int SchemaVersion { get; set; } = 1;

        public int KinopoiskId { get; set; }

        public string State { get; set; } = string.Empty;

        public bool Registered { get; set; }

        public Guid? MovieId { get; set; }

        public string MovieName { get; set; } = string.Empty;

        public Guid? TrailerId { get; set; }

        public int LocalTrailerCount { get; set; }

        public string PlaybackMode { get; set; } = string.Empty;

        public bool CacheEnabled { get; set; }

        public bool CacheReady { get; set; }

        public string CachePath { get; set; } = string.Empty;

        public long CacheEntryBytes { get; set; }

        public long CacheCurrentBytes { get; set; }

        public long CacheMaximumBytes { get; set; }

        public int CacheRetentionDays { get; set; }
    }
}
