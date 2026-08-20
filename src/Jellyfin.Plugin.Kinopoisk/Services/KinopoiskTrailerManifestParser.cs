#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Извлекает HLS-адреса из серверного состояния страницы виджета КиноПоиска.
    /// </summary>
    internal static partial class KinopoiskTrailerManifestParser
    {
        internal static IReadOnlyList<Uri> Extract(string? html)
        {
            if (string.IsNullOrWhiteSpace(html))
                return Array.Empty<Uri>();

            var normalized = NormalizeEscapes(WebUtility.HtmlDecode(html));
            return HlsUrlRegex()
                .Matches(normalized)
                .Select(match => TrimDocumentEnvelope(
                    match.Groups["url"].Value.TrimEnd(')', ']', '}', ',', ';')))
                .Select(value => KinopoiskTrailerUrlPolicy.TryNormalizeManifestUrl(
                    value,
                    out var uri)
                        ? uri
                        : null)
                .Where(uri => uri is not null)
                .DistinctBy(uri => uri!.AbsoluteUri, StringComparer.OrdinalIgnoreCase)
                .Cast<Uri>()
                .OrderByDescending(GetPriority)
                .ToArray();
        }

        private static int GetPriority(Uri uri)
        {
            var value = uri.AbsoluteUri;
            if (value.Contains("master", StringComparison.OrdinalIgnoreCase))
                return 10000;
            if (value.Contains("2160", StringComparison.OrdinalIgnoreCase))
                return 2160;
            if (value.Contains("1440", StringComparison.OrdinalIgnoreCase))
                return 1440;
            if (value.Contains("1080", StringComparison.OrdinalIgnoreCase))
                return 1080;
            if (value.Contains("720", StringComparison.OrdinalIgnoreCase))
                return 720;
            if (value.Contains("480", StringComparison.OrdinalIgnoreCase))
                return 480;
            if (value.Contains("360", StringComparison.OrdinalIgnoreCase))
                return 360;
            return 0;
        }

        private static string NormalizeEscapes(string value)
            => value
                .Replace("\\u002F", "/", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
                .Replace("\\u003D", "=", StringComparison.OrdinalIgnoreCase)
                .Replace("\\/", "/", StringComparison.Ordinal);

        private static string TrimDocumentEnvelope(string value)
        {
            var manifestEnd = value.IndexOf(".m3u8", StringComparison.OrdinalIgnoreCase);
            if (manifestEnd < 0)
                return value;

            var boundary = EncodedDocumentBoundaryRegex().Match(
                value,
                manifestEnd + ".m3u8".Length);
            return boundary.Success
                ? value[..boundary.Index]
                : value;
        }

        [GeneratedRegex(
            "(?<url>https://[^\\s\\\"'<>\\\\]+?\\.m3u8(?:\\?[^\\s\\\"'<>\\\\]*)?)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex HlsUrlRegex();

        [GeneratedRegex(
            "%(?:22|27)(?:%(?:2C|5D|7D)|[,}\\]])",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
        private static partial Regex EncodedDocumentBoundaryRegex();
    }
}
