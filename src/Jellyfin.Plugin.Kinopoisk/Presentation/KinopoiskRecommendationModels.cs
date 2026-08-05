using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    public sealed class KinopoiskSimilarResponse
    {
        public int Total { get; set; }

        public IReadOnlyList<KinopoiskSimilarInfo> Items { get; set; }
            = Array.Empty<KinopoiskSimilarInfo>();
    }

    public sealed class KinopoiskSimilarInfo
    {
        public int KinopoiskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string OriginalName { get; set; } = string.Empty;

        public string PosterUrl { get; set; } = string.Empty;

        public string PosterUrlPreview { get; set; } = string.Empty;

        public string KinopoiskUrl { get; set; } = string.Empty;

        public int? Year { get; set; }

        public double? RatingKinopoisk { get; set; }

        public string Overview { get; set; } = string.Empty;

        public string ImdbId { get; set; } = string.Empty;

        public string MediaType { get; set; } = "movie";
    }
}
