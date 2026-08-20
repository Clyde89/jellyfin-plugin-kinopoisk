using System;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Проверенная локальная копия трейлера.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCacheEntry
    {
        public int SchemaVersion { get; set; } = 1;

        public int KinopoiskId { get; set; }

        public string Path { get; set; } = string.Empty;

        public long ContentLength { get; set; }

        public DateTimeOffset CreatedUtc { get; set; }

        public DateTimeOffset LastAccessUtc { get; set; }

        public string Container { get; set; } = "mp4";

        public string VideoCodec { get; set; } = "h264";

        public string VideoProfile { get; set; } = string.Empty;

        public double? VideoLevel { get; set; }

        public int? VideoBitRate { get; set; }

        public int? Width { get; set; }

        public int? Height { get; set; }

        public float? FrameRate { get; set; }

        public int? VideoBitDepth { get; set; }

        public string PixelFormat { get; set; } = string.Empty;

        public string AudioCodec { get; set; } = "aac";

        public int? AudioBitRate { get; set; }

        public int? AudioChannels { get; set; } = 2;

        public int? AudioSampleRate { get; set; }

        public long? RunTimeTicks { get; set; }

        public int? TotalBitRate { get; set; }
    }

    /// <summary>
    /// Результат плановой очистки локального кэша трейлеров.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCacheCleanupResult
    {
        public int RemovedExpiredFiles { get; set; }

        public int RemovedCapacityFiles { get; set; }

        public int RemovedTemporaryFiles { get; set; }

        public long RemovedBytes { get; set; }

        public long RemainingBytes { get; set; }

        public int RemainingFiles { get; set; }
    }

    /// <summary>
    /// Текущее состояние локального кэша трейлеров.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCacheSnapshot
    {
        public bool Enabled { get; set; }

        public long MaximumBytes { get; set; }

        public int RetentionDays { get; set; }

        public long CurrentBytes { get; set; }

        public int FileCount { get; set; }
    }
}
