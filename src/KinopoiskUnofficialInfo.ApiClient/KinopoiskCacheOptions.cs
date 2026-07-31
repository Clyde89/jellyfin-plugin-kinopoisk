using System;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Содержит параметры кэширования ответов Kinopoisk Unofficial API.
    /// </summary>
    public sealed class KinopoiskCacheOptions
    {
        /// <summary>
        /// Получает или задаёт признак долговременного дискового кэширования.
        /// </summary>
        public bool EnablePersistentCache { get; set; }

        /// <summary>
        /// Получает или задаёт признак использования устаревшего ответа при ошибке API.
        /// </summary>
        public bool UseStaleCacheOnFailure { get; set; }

        /// <summary>
        /// Получает или задаёт каталог долговременного кэша.
        /// </summary>
        public string PersistentCachePath { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт срок хранения основных метаданных.
        /// </summary>
        public TimeSpan MetadataExpiration { get; set; } = TimeSpan.FromHours(12);

        /// <summary>
        /// Получает или задаёт срок хранения списков изображений.
        /// </summary>
        public TimeSpan ImagesExpiration { get; set; } = TimeSpan.FromHours(12);

        /// <summary>
        /// Получает или задаёт срок хранения результатов поиска.
        /// </summary>
        public TimeSpan SearchExpiration { get; set; } = TimeSpan.FromMinutes(15);

        /// <summary>
        /// Получает или задаёт срок хранения пустых результатов.
        /// </summary>
        public TimeSpan EmptyResultExpiration { get; set; } = TimeSpan.FromMinutes(3);

        /// <summary>
        /// Получает или задаёт максимальный размер долговременного кэша в байтах.
        /// </summary>
        public long MaximumPersistentCacheBytes { get; set; } = 512L * 1024L * 1024L;
    }
}
