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
