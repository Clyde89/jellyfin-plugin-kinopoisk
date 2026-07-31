using System;
using System.Xml.Serialization;
using KinopoiskUnofficialInfo.ApiClient;
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
        /// Получает или задаёт источник пользовательского рейтинга Jellyfin.
        /// </summary>
        public CommunityRatingSource CommunityRatingSource { get; set; }
            = CommunityRatingSource.KinopoiskWithImdbFallback;

        /// <summary>
        /// Получает или задаёт источник рейтинга критиков Jellyfin.
        /// </summary>
        public CriticRatingSource CriticRatingSource { get; set; }
            = CriticRatingSource.RussianWithWorldFallback;

        /// <summary>
        /// Получает или задаёт признак загрузки точной даты премьеры из прокатных данных.
        /// </summary>
        public bool EnablePrecisePremiereDate { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак заполнения продолжительности из КиноПоиска.
        /// </summary>
        public bool EnableRuntimeFallback { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак заполнения домашней страницы ссылкой КиноПоиска.
        /// </summary>
        public bool EnableKinopoiskHomePage { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак заполнения статуса и диапазона лет сериала.
        /// </summary>
        public bool EnableSeriesStatus { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак использования краткого описания при отсутствии полного.
        /// </summary>
        public bool EnableShortDescriptionFallback { get; set; } = true;

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
        /// Получает или задаёт максимальный размер дискового кэша ответов API в мегабайтах.
        /// </summary>
        public int PersistentCacheMaximumMegabytes { get; set; } = 512;

        /// <summary>
        /// Получает или задаёт признак локального кэширования бинарных файлов изображений.
        /// </summary>
        public bool EnableImageBinaryCache { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак использования устаревшего изображения при сетевой ошибке.
        /// </summary>
        public bool UseStaleImageCacheOnFailure { get; set; } = true;

        /// <summary>
        /// Получает или задаёт срок актуальности бинарного изображения в днях.
        /// </summary>
        public int ImageBinaryCacheDays { get; set; } = 30;

        /// <summary>
        /// Получает или задаёт максимальный общий размер бинарного кэша изображений в мегабайтах.
        /// </summary>
        public int ImageBinaryCacheMaximumMegabytes { get; set; } = 2048;

        /// <summary>
        /// Получает или задаёт максимальный размер одного кэшируемого изображения в мегабайтах.
        /// </summary>
        public int ImageBinaryCacheMaximumFileMegabytes { get; set; } = 25;

        /// <summary>
        /// Получает или задаёт признак контроля состояния API-ключа и квоты.
        /// </summary>
        public bool EnableQuotaMonitoring { get; set; } = true;

        /// <summary>
        /// Получает или задаёт интервал проверки квоты в часах.
        /// </summary>
        public int QuotaCheckIntervalHours { get; set; } = 6;

        /// <summary>
        /// Возвращает текущую оперативную диагностику без записи в XML-конфигурацию.
        /// </summary>
        [XmlIgnore]
        public KinopoiskDiagnosticsSnapshot Diagnostics
            => KinopoiskDiagnostics.Shared.GetSnapshot();

        /// <summary>
        /// Нормализует значения перед сохранением конфигурации.
        /// </summary>
        public void Normalize()
        {
            ApiToken = ApiToken?.Trim() ?? string.Empty;

            if (!Enum.IsDefined(CommunityRatingSource))
                CommunityRatingSource = CommunityRatingSource.KinopoiskWithImdbFallback;

            if (!Enum.IsDefined(CriticRatingSource))
                CriticRatingSource = CriticRatingSource.RussianWithWorldFallback;

            MetadataCacheHours = Math.Clamp(MetadataCacheHours, 1, 8760);
            ImagesCacheHours = Math.Clamp(ImagesCacheHours, 1, 8760);
            SearchCacheMinutes = Math.Clamp(SearchCacheMinutes, 1, 10080);
            NegativeCacheMinutes = Math.Clamp(NegativeCacheMinutes, 1, 1440);
            PersistentCacheMaximumMegabytes = Math.Clamp(
                PersistentCacheMaximumMegabytes,
                64,
                8192);
            ImageBinaryCacheDays = Math.Clamp(ImageBinaryCacheDays, 1, 3650);
            ImageBinaryCacheMaximumMegabytes = Math.Clamp(
                ImageBinaryCacheMaximumMegabytes,
                64,
                16384);
            ImageBinaryCacheMaximumFileMegabytes = Math.Clamp(
                ImageBinaryCacheMaximumFileMegabytes,
                1,
                100);
            QuotaCheckIntervalHours = Math.Clamp(QuotaCheckIntervalHours, 1, 168);
        }
    }
}
