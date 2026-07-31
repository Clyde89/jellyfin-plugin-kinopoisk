using Newtonsoft.Json;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Дополняет сгенерированную модель фильма полями актуальной версии API.
    /// </summary>
    public partial class Film
    {
        /// <summary>
        /// Получает или задаёт альтернативную обложку.
        /// </summary>
        [JsonProperty("coverUrl", Required = Required.Default)]
        public string CoverUrl { get; set; }

        /// <summary>
        /// Получает или задаёт прозрачный логотип фильма или сериала.
        /// </summary>
        [JsonProperty("logoUrl", Required = Required.Default)]
        public string LogoUrl { get; set; }
    }
}
