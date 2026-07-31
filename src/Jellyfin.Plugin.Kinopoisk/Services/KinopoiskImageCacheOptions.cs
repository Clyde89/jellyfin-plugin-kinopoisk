using System;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит параметры локального бинарного кэша изображений.
    /// </summary>
    public sealed class KinopoiskImageCacheOptions
    {
        /// <summary>
        /// Получает или задаёт признак кэширования бинарных файлов изображений.
        /// </summary>
        public bool Enabled { get; set; }

        /// <summary>
        /// Получает или задаёт признак использования устаревшего изображения при сетевой ошибке.
        /// </summary>
        public bool UseStaleOnFailure { get; set; }

        /// <summary>
        /// Получает или задаёт каталог бинарного кэша.
        /// </summary>
        public string CachePath { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт срок актуальности изображения.
        /// </summary>
        public TimeSpan Expiration { get; set; } = TimeSpan.FromDays(30);

        /// <summary>
        /// Получает или задаёт максимальный общий размер кэша в байтах.
        /// </summary>
        public long MaximumCacheBytes { get; set; } = 2L * 1024L * 1024L * 1024L;

        /// <summary>
        /// Получает или задаёт максимальный размер одного изображения в байтах.
        /// </summary>
        public long MaximumFileBytes { get; set; } = 25L * 1024L * 1024L;
    }
}
