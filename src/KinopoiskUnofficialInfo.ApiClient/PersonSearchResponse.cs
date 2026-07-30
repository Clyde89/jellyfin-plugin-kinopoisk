using System;
using System.Collections.Generic;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Представляет результат поиска персон по имени.
    /// </summary>
    public sealed class PersonSearchResponse
    {
        public int Total { get; set; }

        public ICollection<PersonSearchItem> Items { get; set; } = Array.Empty<PersonSearchItem>();
    }

    /// <summary>
    /// Представляет найденную персону КиноПоиска.
    /// </summary>
    public sealed class PersonSearchItem
    {
        public int KinopoiskId { get; set; }

        public string WebUrl { get; set; }

        public string NameRu { get; set; }

        public string NameEn { get; set; }

        public string Sex { get; set; }

        public string PosterUrl { get; set; }
    }
}
