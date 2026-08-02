using System;
using System.Collections.Generic;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    /// <summary>
    /// Содержит основные данные расширенной карточки КиноПоиска.
    /// </summary>
    public sealed class KinopoiskPresentationResponse
    {
        public int KinopoiskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string OriginalName { get; set; } = string.Empty;

        public string ImdbId { get; set; } = string.Empty;

        public string KinopoiskUrl { get; set; } = string.Empty;

        public string ImdbUrl { get; set; } = string.Empty;

        public KinopoiskRatingInfo Ratings { get; set; } = new();

        public IReadOnlyList<KinopoiskReleaseDateInfo> ReleaseDates { get; set; }
            = Array.Empty<KinopoiskReleaseDateInfo>();

        public IReadOnlyList<KinopoiskProfessionGroup> Professions { get; set; }
            = Array.Empty<KinopoiskProfessionGroup>();

        public IReadOnlyList<KinopoiskRelationInfo> Relations { get; set; }
            = Array.Empty<KinopoiskRelationInfo>();
    }

    public sealed class KinopoiskRatingInfo
    {
        public double? Kinopoisk { get; set; }

        public int KinopoiskVotes { get; set; }

        public double? Imdb { get; set; }

        public int ImdbVotes { get; set; }

        public double? RussianCritics { get; set; }

        public int RussianCriticsVotes { get; set; }

        public double? WorldCritics { get; set; }

        public int WorldCriticsVotes { get; set; }
    }

    public sealed class KinopoiskReleaseDateInfo
    {
        public string Type { get; set; } = string.Empty;

        public string SubType { get; set; } = string.Empty;

        public string Date { get; set; } = string.Empty;

        public string Country { get; set; } = string.Empty;

        public bool ReRelease { get; set; }

        public string Source { get; set; } = "КиноПоиск";
    }

    public sealed class KinopoiskProfessionGroup
    {
        public string Key { get; set; } = string.Empty;

        public string Label { get; set; } = string.Empty;

        public IReadOnlyList<KinopoiskPersonInfo> People { get; set; }
            = Array.Empty<KinopoiskPersonInfo>();
    }

    public sealed class KinopoiskPersonInfo
    {
        public int KinopoiskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string OriginalName { get; set; } = string.Empty;

        public string Profession { get; set; } = string.Empty;

        public string PosterUrl { get; set; } = string.Empty;
    }

    public sealed class KinopoiskRelationInfo
    {
        public int KinopoiskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string OriginalName { get; set; } = string.Empty;

        public string RelationType { get; set; } = string.Empty;

        public string PosterUrl { get; set; } = string.Empty;

        public string KinopoiskUrl { get; set; } = string.Empty;
    }

    public sealed class KinopoiskFactsResponse
    {
        public int Total { get; set; }

        public IReadOnlyList<KinopoiskFactInfo> Items { get; set; }
            = Array.Empty<KinopoiskFactInfo>();
    }

    public sealed class KinopoiskFactInfo
    {
        public string Text { get; set; } = string.Empty;

        public string Type { get; set; } = string.Empty;

        public bool Spoiler { get; set; }
    }

    public sealed class KinopoiskBoxOfficeResponse
    {
        public int Total { get; set; }

        public IReadOnlyList<KinopoiskBoxOfficeInfo> Items { get; set; }
            = Array.Empty<KinopoiskBoxOfficeInfo>();
    }

    public sealed class KinopoiskBoxOfficeInfo
    {
        public string Type { get; set; } = string.Empty;

        public decimal Amount { get; set; }

        public string Currency { get; set; } = string.Empty;

        public string Symbol { get; set; } = string.Empty;
    }

    public sealed class KinopoiskAwardsResponse
    {
        public int Total { get; set; }

        public IReadOnlyList<KinopoiskAwardInfo> Items { get; set; }
            = Array.Empty<KinopoiskAwardInfo>();
    }

    public sealed class KinopoiskAwardInfo
    {
        public string Name { get; set; } = string.Empty;

        public string NominationName { get; set; } = string.Empty;

        public int Year { get; set; }

        public bool Win { get; set; }

        public string ImageUrl { get; set; } = string.Empty;

        public IReadOnlyList<KinopoiskAwardPersonInfo> Persons { get; set; }
            = Array.Empty<KinopoiskAwardPersonInfo>();
    }

    public sealed class KinopoiskAwardPersonInfo
    {
        public int KinopoiskId { get; set; }

        public string Name { get; set; } = string.Empty;

        public string OriginalName { get; set; } = string.Empty;

        public string Profession { get; set; } = string.Empty;
    }
}
