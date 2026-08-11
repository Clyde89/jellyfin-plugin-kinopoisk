#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлено безопасное преобразование HTML Jellyfin Web в памяти процесса.
    /// </summary>
    internal static class KinopoiskWebBootstrapTransformer
    {
        internal const string BeginMarker = "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->";
        internal const string EndMarker = "<!-- KINOPOISK_WEB_CLIENT_END -->";
        internal const string WebClientPath = "../Kinopoisk/WebClient.js";
        internal const string RuntimeManagedAttribute =
            "data-kinopoisk-managed=\"runtime\"";

        private static readonly UTF8Encoding StrictUtf8 = new(false, true);

        /// <summary>
        /// Сформирован HTML с единственным runtime-блоком КиноПоиска.
        /// </summary>
        /// <param name="value">Исходный HTML Jellyfin Web.</param>
        /// <returns>Преобразованный HTML.</returns>
        internal static string Transform(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                throw new InvalidDataException("Содержимое index.html не задано.");
            }

            var clean = RemoveManagedBlock(value);
            if (CountOccurrences(clean, WebClientPath) != 0)
            {
                throw new InvalidDataException(
                    "Обнаружено неуправляемое подключение WebClient.js КиноПоиска.");
            }

            var bodyEnd = clean.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < 0)
            {
                throw new InvalidDataException("Закрывающий тег body в index.html не найден.");
            }

            var scriptElement = string.Concat(
                "    <script src=\"",
                WebClientPath,
                "?v=",
                KinopoiskWebClientBundle.VersionToken,
                "\" defer ",
                RuntimeManagedAttribute,
                "></script>");
            var block = string.Join(
                Environment.NewLine,
                BeginMarker,
                scriptElement,
                EndMarker);
            var result = clean.Insert(bodyEnd, block + Environment.NewLine);
            ValidateRuntimeIndex(result);
            return result;
        }

        /// <summary>
        /// Удалён ранее управляемый блок КиноПоиска без изменения остального документа.
        /// </summary>
        /// <param name="value">HTML с возможным управляемым блоком.</param>
        /// <returns>HTML без управляемого блока.</returns>
        internal static string RemoveManagedBlock(string value)
        {
            var beginCount = CountOccurrences(value, BeginMarker);
            var endCount = CountOccurrences(value, EndMarker);

            if (beginCount == 0 && endCount == 0)
            {
                return value;
            }

            if (beginCount != 1 || endCount != 1)
            {
                throw new InvalidDataException(
                    "Управляемые маркеры КиноПоиска повреждены или продублированы.");
            }

            var start = value.IndexOf(BeginMarker, StringComparison.Ordinal);
            var end = value.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < start)
            {
                throw new InvalidDataException(
                    "Конечный маркер КиноПоиска расположен раньше начального.");
            }

            end += EndMarker.Length;
            while (end < value.Length && (value[end] == '\r' || value[end] == '\n'))
            {
                end++;
            }

            return value.Remove(start, end - start);
        }

        /// <summary>
        /// Подтверждена корректность единственного runtime-блока.
        /// </summary>
        /// <param name="value">Преобразованный HTML.</param>
        internal static void ValidateRuntimeIndex(string value)
        {
            if (CountOccurrences(value, BeginMarker) != 1
                || CountOccurrences(value, EndMarker) != 1
                || CountOccurrences(value, WebClientPath) != 1
                || CountOccurrences(value, RuntimeManagedAttribute) != 1
                || CountOccurrences(value, string.Concat("?v=", KinopoiskWebClientBundle.VersionToken)) != 1)
            {
                throw new InvalidDataException(
                    "Runtime-блок автономного веб-клиента КиноПоиска сформирован некорректно.");
            }

            var markerStart = value.IndexOf(BeginMarker, StringComparison.Ordinal);
            var markerEnd = value.IndexOf(EndMarker, markerStart, StringComparison.Ordinal);
            var scriptStart = value.IndexOf(WebClientPath, markerStart, StringComparison.Ordinal);
            var attributeStart = value.IndexOf(
                RuntimeManagedAttribute,
                markerStart,
                StringComparison.Ordinal);

            if (!(markerStart < scriptStart
                && scriptStart < markerEnd
                && markerStart < attributeStart
                && attributeStart < markerEnd))
            {
                throw new InvalidDataException(
                    "Endpoint или признак runtime-управления расположены вне блока КиноПоиска.");
            }
        }

        /// <summary>
        /// Преобразованы UTF-8 байты HTML без записи на диск.
        /// </summary>
        /// <param name="value">Исходные UTF-8 байты.</param>
        /// <returns>Преобразованные UTF-8 байты.</returns>
        internal static byte[] TransformUtf8(ReadOnlySpan<byte> value)
        {
            var source = StrictUtf8.GetString(value);
            return StrictUtf8.GetBytes(Transform(source));
        }

        /// <summary>
        /// Рассчитан сильный ETag преобразованного HTML.
        /// </summary>
        /// <param name="value">Преобразованные байты HTML.</param>
        /// <returns>Значение HTTP ETag.</returns>
        internal static string ComputeEtag(ReadOnlySpan<byte> value)
        {
            var digest = SHA256.HashData(value);
            return string.Concat(
                '"',
                "kp-",
                Convert.ToHexString(digest).ToLowerInvariant(),
                '"');
        }

        internal static int CountOccurrences(string value, string token)
        {
            var count = 0;
            var offset = 0;
            while (true)
            {
                var index = value.IndexOf(token, offset, StringComparison.Ordinal);
                if (index < 0)
                {
                    return count;
                }

                count++;
                offset = index + token.Length;
            }
        }
    }
}
