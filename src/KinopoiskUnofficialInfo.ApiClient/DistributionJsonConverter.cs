using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Дополняет сгенерированную модель признаком фактического наличия подтипа проката.
    /// </summary>
    [JsonConverter(typeof(DistributionJsonConverter))]
    public partial class Distribution
    {
        /// <summary>
        /// Получает признак наличия непустого значения <c>subType</c> в ответе API.
        /// </summary>
        [JsonIgnore]
        public bool HasSubType { get; internal set; }
    }

    /// <summary>
    /// Разбирает прокатные данные, в которых API может вернуть <c>subType: null</c>.
    /// </summary>
    internal sealed class DistributionJsonConverter : JsonConverter
    {
        private static readonly HashSet<string> KnownProperties = new(StringComparer.OrdinalIgnoreCase)
        {
            "type",
            "subType",
            "date",
            "reRelease",
            "country",
            "companies"
        };

        public override bool CanWrite => false;

        public override bool CanConvert(System.Type objectType)
            => objectType == typeof(Distribution);

        public override object? ReadJson(
            JsonReader reader,
            System.Type objectType,
            object? existingValue,
            JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.Null)
                return null;

            var source = JObject.Load(reader);
            var result = new Distribution
            {
                Type = ParseEnum(
                    source["type"],
                    DistributionType.ALL),
                Date = source.Value<string>("date") ?? string.Empty,
                ReRelease = source.Value<bool?>("reRelease") ?? false,
                Country = source["country"]?.ToObject<Country>(serializer) ?? new Country(),
                Companies = source["companies"]?.ToObject<ICollection<Company>>(serializer)
                    ?? new Collection<Company>()
            };

            if (TryParseEnum(source["subType"], out DistributionSubType subType))
            {
                result.SubType = subType;
                result.HasSubType = true;
            }
            else
            {
                // Значение использовано только как безопасный заполнитель. Логика выбора даты
                // учитывает HasSubType и не трактует заполнитель как реальный подтип.
                result.SubType = DistributionSubType.DIGITAL;
                result.HasSubType = false;
            }

            foreach (var property in source.Properties())
            {
                if (KnownProperties.Contains(property.Name))
                    continue;

                result.AdditionalProperties[property.Name] =
                    property.Value.ToObject<object>(serializer);
            }

            return result;
        }

        public override void WriteJson(JsonWriter writer, object? value, JsonSerializer serializer)
            => throw new NotSupportedException("Запись прокатных данных этим преобразователем не поддерживается.");

        private static TEnum ParseEnum<TEnum>(JToken? token, TEnum fallback)
            where TEnum : struct, Enum
            => TryParseEnum(token, out TEnum value) ? value : fallback;

        private static bool TryParseEnum<TEnum>(JToken? token, out TEnum value)
            where TEnum : struct, Enum
        {
            value = default;
            if (token is null || token.Type == JTokenType.Null)
                return false;

            var text = token.Value<string>();
            return !string.IsNullOrWhiteSpace(text)
                && Enum.TryParse(text.Trim(), true, out value);
        }
    }
}
