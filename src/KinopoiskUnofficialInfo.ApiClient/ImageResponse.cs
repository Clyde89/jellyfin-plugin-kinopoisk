using System;
using System.Collections.Generic;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Представляет страницу изображений фильма или сериала.
    /// </summary>
    public sealed class ImageResponse
    {
        public int Total { get; set; }

        public int TotalPages { get; set; }

        public ICollection<ImageResponseItem> Items { get; set; } = Array.Empty<ImageResponseItem>();
    }

    /// <summary>
    /// Представляет одно изображение КиноПоиска.
    /// </summary>
    public sealed class ImageResponseItem
    {
        public string ImageUrl { get; set; }

        public string PreviewUrl { get; set; }
    }
}
