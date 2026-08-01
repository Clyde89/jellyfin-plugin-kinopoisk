using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Сохраняет исходную профессию участника и предотвращает ошибочное представление оператора режиссёром.
    /// </summary>
    [JsonConverter(typeof(StaffResponseJsonConverter))]
    public partial class StaffResponse
    {
        /// <summary>
        /// Получает профессию, фактически полученную от API.
        /// </summary>
        [JsonIgnore]
        public StaffResponseProfessionKey OriginalProfessionKey { get; internal set; }
    }

    /// <summary>
    /// Выполняет устойчивый разбор сведений об участнике.
    /// </summary>
    internal sealed class StaffResponseJsonConverter : JsonConverter
    {
        private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "staffId",
            "nameRu",
            "nameEn",
            "posterUrl",
            "professionText",
            "professionKey"
        };

        public override bool CanWrite => false;

        public override bool CanConvert(Type objectType)
            => objectType == typeof(StaffResponse);

        public override object ReadJson(
            JsonReader reader,
            Type objectType,
            object existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            var source = JObject.Load(reader);
            var originalProfession = ParseProfession(source["professionKey"]);
            var result = new StaffResponse
            {
                StaffId = source.Value<int?>("staffId") ?? 0,
                NameRu = source.Value<string>("nameRu") ?? string.Empty,
                NameEn = source.Value<string>("nameEn") ?? string.Empty,
                PosterUrl = source.Value<string>("posterUrl") ?? string.Empty,
                ProfessionText = source.Value<string>("professionText") ?? string.Empty,
                OriginalProfessionKey = originalProfession,
                ProfessionKey = originalProfession == StaffResponseProfessionKey.OPERATOR
                    ? StaffResponseProfessionKey.UNKNOWN
                    : originalProfession
            };

            foreach (var property in source.Properties())
            {
                if (KnownProperties.Contains(property.Name))
                    continue;

                result.AdditionalProperties[property.Name] =
                    property.Value.ToObject<object>(serializer);
            }

            return result;
        }

        public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            => throw new NotSupportedException("Запись сведений об участнике этим преобразователем не поддерживается.");

        private static StaffResponseProfessionKey ParseProfession(JToken token)
        {
            var text = token?.Value<string>();
            return !string.IsNullOrWhiteSpace(text)
                && Enum.TryParse(text.Trim(), true, out StaffResponseProfessionKey value)
                    ? value
                    : StaffResponseProfessionKey.UNKNOWN;
        }
    }
}
