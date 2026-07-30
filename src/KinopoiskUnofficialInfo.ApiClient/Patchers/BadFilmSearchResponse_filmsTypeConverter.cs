using System;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public class BadFilmSearchResponse_filmsTypeConverter : StringEnumConverter
    {
        public override bool CanConvert(System.Type objectType)
            => objectType == typeof(FilmSearchResponse_filmsType);

        public override object ReadJson(JsonReader reader, System.Type objectType, object existingValue, JsonSerializer serializer)
        {
            if (reader.TokenType == JsonToken.String)
            {
                var sourceType = ((string)reader.Value)?.ToUpperInvariant();

                if (sourceType == "VIDEO")
                    return FilmSearchResponse_filmsType.FILM;

                if (sourceType == "MINI_SERIES" || sourceType == "TV_SERIES")
                    return FilmSearchResponse_filmsType.TV_SHOW;
            }

            try
            {
                return base.ReadJson(reader, objectType, existingValue, serializer);
            }
            catch (Exception)
            {
                return FilmSearchResponse_filmsType.UNKNOWN;
            }
        }
    }
}
