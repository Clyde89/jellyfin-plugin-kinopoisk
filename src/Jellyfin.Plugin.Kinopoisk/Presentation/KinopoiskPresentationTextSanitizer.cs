#nullable enable

using System;
using System.Net;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    /// <summary>
    /// Выполнена безопасная нормализация текстовых значений внешнего API.
    /// </summary>
    internal static partial class KinopoiskPresentationTextSanitizer
    {
        /// <summary>
        /// Выполнено удаление HTML-разметки с сохранением читаемого текста.
        /// </summary>
        /// <param name="value">Исходное значение.</param>
        /// <returns>Нормализованный текст без исполняемой разметки.</returns>
        public static string NormalizePlainText(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var text = LineBreakTagRegex().Replace(value, "\n");
            text = BlockBoundaryTagRegex().Replace(text, "\n");
            text = HtmlTagRegex().Replace(text, string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = text.Replace('\u00A0', ' ');
            text = HorizontalWhitespaceRegex().Replace(text, " ");
            text = SurroundingLineWhitespaceRegex().Replace(text, "\n");
            text = ExcessiveLineBreakRegex().Replace(text, "\n\n");
            return text.Trim();
        }

        [GeneratedRegex(@"(?is)<\s*br\s*/?\s*>", RegexOptions.CultureInvariant)]
        private static partial Regex LineBreakTagRegex();

        [GeneratedRegex(
            @"(?is)</?\s*(?:p|div|li|ul|ol|h[1-6]|blockquote|section|article)\b[^>]*>",
            RegexOptions.CultureInvariant)]
        private static partial Regex BlockBoundaryTagRegex();

        [GeneratedRegex(@"(?is)<[^>]+>", RegexOptions.CultureInvariant)]
        private static partial Regex HtmlTagRegex();

        [GeneratedRegex(@"[\t\f\v ]+", RegexOptions.CultureInvariant)]
        private static partial Regex HorizontalWhitespaceRegex();

        [GeneratedRegex(@" *\r?\n *", RegexOptions.CultureInvariant)]
        private static partial Regex SurroundingLineWhitespaceRegex();

        [GeneratedRegex(@"(?:\r?\n){3,}", RegexOptions.CultureInvariant)]
        private static partial Regex ExcessiveLineBreakRegex();
    }
}
