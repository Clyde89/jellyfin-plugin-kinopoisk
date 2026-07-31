using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kinopoisk.Configuration
{
    /// <summary>
    /// Содержит параметры поставщика метаданных КиноПоиска.
    /// </summary>
    public class PluginConfiguration : BasePluginConfiguration
    {
        /// <summary>
        /// Получает или задаёт персональный токен Kinopoisk Unofficial API.
        /// </summary>
        public string ApiToken { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт признак загрузки метаданных фильмов.
        /// </summary>
        public bool EnableMovieMetadata { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак загрузки метаданных сериалов.
        /// </summary>
        public bool EnableSeriesMetadata { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак загрузки метаданных сезонов и эпизодов.
        /// </summary>
        public bool EnableSeasonEpisodeMetadata { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак загрузки участников и метаданных персон.
        /// </summary>
        public bool EnablePeopleMetadata { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак загрузки удалённых трейлеров.
        /// </summary>
        public bool EnableTrailers { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак загрузки удалённых изображений.
        /// </summary>
        public bool EnableImages { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак долговременного дискового кэширования ответов API.
        /// </summary>
        public bool EnablePersistentCache { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак использования устаревшего дискового кэша при ошибке API.
        /// </summary>
        public bool UseStaleCacheOnFailure { get; set; } = true;

        /// <summary>
        /// Получает или задаёт срок хранения основных метаданных в часах.
        /// </summary>
        public int MetadataCacheHours { get; set; } = 168;

        /// <summary>
        /// Получает или задаёт срок хранения ответов со списками изображений в часах.
        /// </summary>
        public int ImagesCacheHours { get; set; } = 720;

        /// <summary>
        /// Получает или задаёт срок хранения результатов поиска в минутах.
        /// </summary>
        public int SearchCacheMinutes { get; set; } = 60;

        /// <summary>
        /// Получает или задаёт срок хранения пустых результатов в минутах.
        /// </summary>
        public int NegativeCacheMinutes { get; set; } = 15;

        /// <summary>
        /// Получает или задаёт максимальный размер дискового кэша в мегабайтах.
        /// </summary>
        public int PersistentCacheMaximumMegabytes { get; set; } = 512;

        /// <summary>
        /// Получает или задаёт признак контроля состояния API-ключа и квоты.
        /// </summary>
        public bool EnableQuotaMonitoring { get; set; } = true;

        /// <summary>
        /// Получает или задаёт интервал проверки квоты в часах.
        /// </summary>
        public int QuotaCheckIntervalHours { get; set; } = 6;
    }
}
