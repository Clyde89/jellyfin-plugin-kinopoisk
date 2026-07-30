using System;
using System.Globalization;

namespace Jellyfin.Plugin.Kinopoisk
{
    /// <summary>
    /// Предоставляет безопасный разбор дат, возвращаемых API КиноПоиска.
    /// </summary>
    public static class KinopoiskDateExtensions
    {
        private const string DateFormat = "yyyy-MM-dd";

        public static DateTime? ParseKinopoiskDate(this string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;

            var normalizedValue = value.Trim();
            if (DateTime.TryParseExact(
                normalizedValue,
                DateFormat,
                CultureInfo.InvariantCulture,
                DateTimeStyles.None,
                out var exactDate))
            {
                return exactDate;
            }

            return DateTime.TryParse(
                normalizedValue,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var parsedDate)
                ? parsedDate
                : null;
        }
    }
}
