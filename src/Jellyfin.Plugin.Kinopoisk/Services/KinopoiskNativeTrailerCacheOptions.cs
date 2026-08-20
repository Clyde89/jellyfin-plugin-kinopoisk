using System;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Определяет момент предварительной подготовки локального MP4-трейлера.
    /// </summary>
    public enum KinopoiskTrailerCachePopulationMode
    {
        /// <summary>
        /// Подготавливать трейлер только после открытия карточки фильма.
        /// </summary>
        OnDemand,

        /// <summary>
        /// Подготавливать трейлер вместе с получением метаданных фильма.
        /// </summary>
        DuringMetadataScan
    }

    /// <summary>
    /// Определяет клиентов, для которых автоматически регистрируется LocalTrailer.
    /// </summary>
    public enum KinopoiskTrailerCacheClientScope
    {
        /// <summary>
        /// Использовать единый серверный трейлер во всех клиентах Jellyfin.
        /// </summary>
        AllClients,

        /// <summary>
        /// Автоматически регистрировать серверный трейлер только для Android TV.
        /// </summary>
        AndroidTvOnly
    }

    /// <summary>
    /// Параметры локального кэша трейлеров для нативных клиентов Jellyfin.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCacheOptions
    {
        public bool Enabled { get; set; } = true;

        public string CachePath { get; set; } = string.Empty;

        public long MaximumCacheBytes { get; set; } = 4L * 1024L * 1024L * 1024L;

        public long MaximumFileBytes { get; set; } = 512L * 1024L * 1024L;

        public TimeSpan UnusedExpiration { get; set; } = TimeSpan.FromDays(30);

        public TimeSpan RemuxTimeout { get; set; } = TimeSpan.FromMinutes(10);

        public TimeSpan PlaybackStartupWait { get; set; } = TimeSpan.FromSeconds(25);
    }
}
