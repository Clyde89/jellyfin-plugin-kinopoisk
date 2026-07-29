using System;
using System.Collections.Generic;
using Newtonsoft.Json;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public sealed class FilmSearchQuery
    {
        public string ImdbId { get; set; }

        public string Keyword { get; set; }

        public int? YearFrom { get; set; }

        public int? YearTo { get; set; }

        public string Type { get; set; }

        public int Page { get; set; } = 1;
    }

    public sealed class FilteredFilmSearchResponse
    {
        [JsonProperty("total")]
        public int Total { get; set; }

        [JsonProperty("totalPages")]
        public int TotalPages { get; set; }

        [JsonProperty("items")]
        public ICollection<FilteredFilmSearchItem> Items { get; set; } = Array.Empty<FilteredFilmSearchItem>();
    }

    public sealed class FilteredFilmSearchItem
    {
        [JsonProperty("kinopoiskId")]
        public int KinopoiskId { get; set; }

        [JsonProperty("imdbId")]
        public string ImdbId { get; set; }

        [JsonProperty("nameRu")]
        public string NameRu { get; set; }

        [JsonProperty("nameEn")]
        public string NameEn { get; set; }

        [JsonProperty("nameOriginal")]
        public string NameOriginal { get; set; }

        [JsonProperty("year")]
        public int? Year { get; set; }

        [JsonProperty("type")]
        public string Type { get; set; }

        [JsonProperty("posterUrl")]
        public string PosterUrl { get; set; }

        [JsonProperty("posterUrlPreview")]
        public string PosterUrlPreview { get; set; }
    }
}
