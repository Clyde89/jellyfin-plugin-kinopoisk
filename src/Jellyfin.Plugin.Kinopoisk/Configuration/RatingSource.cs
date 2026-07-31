namespace Jellyfin.Plugin.Kinopoisk.Configuration
{
    /// <summary>
    /// Определяет источник пользовательского рейтинга Jellyfin.
    /// </summary>
    public enum CommunityRatingSource
    {
        /// <summary>
        /// Использован рейтинг КиноПоиска, а IMDb применён как резерв.
        /// </summary>
        KinopoiskWithImdbFallback = 0,

        /// <summary>
        /// Использован только рейтинг КиноПоиска.
        /// </summary>
        KinopoiskOnly = 1,

        /// <summary>
        /// Использован рейтинг IMDb, а КиноПоиск применён как резерв.
        /// </summary>
        ImdbWithKinopoiskFallback = 2,

        /// <summary>
        /// Использован только рейтинг IMDb.
        /// </summary>
        ImdbOnly = 3,

        /// <summary>
        /// Пользовательский рейтинг не заполнен поставщиком.
        /// </summary>
        Disabled = 4
    }

    /// <summary>
    /// Определяет источник рейтинга критиков Jellyfin.
    /// </summary>
    public enum CriticRatingSource
    {
        /// <summary>
        /// Использован рейтинг российских критиков, а мировой применён как резерв.
        /// </summary>
        RussianWithWorldFallback = 0,

        /// <summary>
        /// Использован только рейтинг российских критиков.
        /// </summary>
        RussianOnly = 1,

        /// <summary>
        /// Использован мировой рейтинг критиков, а российский применён как резерв.
        /// </summary>
        WorldWithRussianFallback = 2,

        /// <summary>
        /// Использован только мировой рейтинг критиков.
        /// </summary>
        WorldOnly = 3,

        /// <summary>
        /// Рейтинг критиков не заполнен поставщиком.
        /// </summary>
        Disabled = 4
    }
}
