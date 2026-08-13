using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Нормализует, сортирует и ограничивает трейлеры, полученные через API КиноПоиска.
    /// </summary>
    public static partial class KinopoiskTrailerSelector
    {
        private static readonly string[] OfficialMarkers =
        {
            "официаль", "official"
        };

        private static readonly string[] RussianMarkers =
        {
            "русск", "дублирован", "дубляж", "озвучк", "russian", "dubbed"
        };

        private static readonly string[] TrailerMarkers =
        {
            "трейлер", "trailer"
        };

        private static readonly string[] TeaserMarkers =
        {
            "тизер", "teaser"
        };

        private static readonly string[] NonTrailerMarkers =
        {
            "фрагмент", "отрывок", "интервью", "за кадром", "съёмк", "клип",
            "featurette", "behind the scenes", "making of", "interview", "clip", "tv spot"
        };

        private static readonly string[] UnofficialMarkers =
        {
            "неофициаль", "фан", "fan made", "fan-made", "concept", "fake"
        };

        private static readonly string[] KinopoiskWidgetHosts =
        {
            "widgets.kinopoisk.ru"
        };

        private static readonly string[] YandexDiskHosts =
        {
            "disk.yandex.ru",
            "disk.yandex.com",
            "yadi.sk"
        };

        /// <summary>
        /// Возвращает поддерживаемые Jellyfin трейлеры в приоритетном порядке.
        /// </summary>
        /// <param name="response">Ответ API КиноПоиска.</param>
        /// <param name="options">Параметры отбора.</param>
        /// <returns>Безопасные ссылки поддерживаемых источников.</returns>
        public static IReadOnlyList<MediaUrl> Select(
            VideoResponse response,
            KinopoiskTrailerSelectionOptions options)
        {
            options ??= new KinopoiskTrailerSelectionOptions();
            var maximumTrailers = Math.Clamp(options.MaximumTrailers, 1, 20);
            var items = response?.Items?.Where(item => item is not null).ToArray()
                ?? Array.Empty<VideoResponse_items>();

            if (items.Length == 0)
            {
                RecordSelectionDiagnostics(items, Array.Empty<TrailerCandidate>());
                return Array.Empty<MediaUrl>();
            }

            var candidates = items
                .Select((item, index) => CreateCandidate(item, index, options))
                .Where(candidate => candidate is not null)
                .ToArray();

            var selected = candidates
                .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.OriginalIndex)
                    .First())
                .OrderBy(candidate => candidate.SourcePriority)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.OriginalIndex)
                .Take(maximumTrailers)
                .ToArray();

            RecordSelectionDiagnostics(items, selected);

            return selected
                .Select(candidate => new MediaUrl
                {
                    Name = candidate.DisplayName,
                    Url = candidate.CanonicalUrl
                })
                .ToArray();
        }

        /// <summary>
        /// Возвращает нормализованные кандидаты вместе с типом источника.
        /// </summary>
        internal static IReadOnlyList<KinopoiskTrailerCandidate> SelectCandidates(
            VideoResponse response,
            KinopoiskTrailerSelectionOptions options)
        {
            options ??= new KinopoiskTrailerSelectionOptions();
            var items = response?.Items?.Where(item => item is not null).ToArray()
                ?? Array.Empty<VideoResponse_items>();

            return items
                .Select((item, index) => CreateCandidate(item, index, options))
                .Where(candidate => candidate is not null)
                .GroupBy(candidate => candidate.Identity, StringComparer.OrdinalIgnoreCase)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.OriginalIndex)
                    .First())
                .OrderBy(candidate => candidate.SourcePriority)
                .ThenByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.OriginalIndex)
                .Select(candidate => new KinopoiskTrailerCandidate(
                    candidate.CanonicalUrl,
                    candidate.DisplayName,
                    candidate.SourceKind))
                .ToArray();
        }

        /// <summary>
        /// Преобразует поддерживаемую YouTube-ссылку в единый канонический формат.
        /// </summary>
        /// <param name="url">Исходная ссылка.</param>
        /// <param name="videoId">Идентификатор ролика.</param>
        /// <param name="canonicalUrl">Каноническая ссылка.</param>
        /// <returns><see langword="true" />, если ссылка распознана безопасно.</returns>
        public static bool TryNormalizeYoutubeUrl(
            string url,
            out string videoId,
            out string canonicalUrl)
        {
            videoId = null;
            canonicalUrl = null;

            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            string candidateId = null;

            if (host == "youtu.be")
            {
                candidateId = GetFirstPathSegment(uri.AbsolutePath);
            }
            else if (host == "youtube.com"
                || host.EndsWith(".youtube.com", StringComparison.Ordinal)
                || host == "youtube-nocookie.com"
                || host.EndsWith(".youtube-nocookie.com", StringComparison.Ordinal))
            {
                var segments = uri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (segments.Length > 0
                    && string.Equals(segments[0], "watch", StringComparison.OrdinalIgnoreCase))
                {
                    candidateId = GetQueryValue(uri.Query, "v");
                }
                else if (segments.Length > 1
                    && (string.Equals(segments[0], "embed", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(segments[0], "v", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(segments[0], "shorts", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(segments[0], "live", StringComparison.OrdinalIgnoreCase)))
                {
                    candidateId = segments[1];
                }
                else
                {
                    candidateId = GetQueryValue(uri.Query, "v");
                }
            }

            candidateId = candidateId?.Trim();
            if (string.IsNullOrWhiteSpace(candidateId)
                || !YoutubeVideoIdRegex().IsMatch(candidateId))
            {
                return false;
            }

            videoId = candidateId;
            canonicalUrl = "https://www.youtube.com/watch?v=" + candidateId;
            return true;
        }

        private static TrailerCandidate CreateCandidate(
            VideoResponse_items item,
            int index,
            KinopoiskTrailerSelectionOptions options)
        {
            if (!TryNormalizeSupportedUrl(
                    item,
                    out var identity,
                    out var canonicalUrl,
                    out var sourcePriority,
                    out var sourceKind))
            {
                return null;
            }

            var originalName = string.IsNullOrWhiteSpace(item.Name)
                ? "Трейлер"
                : NormalizeWhitespace(item.Name);
            var normalizedName = originalName.ToLowerInvariant();
            var isTeaser = ContainsAny(normalizedName, TeaserMarkers);
            var isNonTrailer = ContainsAny(normalizedName, NonTrailerMarkers);

            if (isTeaser && !options.IncludeTeasers)
                return null;

            if (isNonTrailer && !options.IncludeAdditionalVideos)
                return null;

            var score = sourcePriority;
            var isOfficial = ContainsAny(normalizedName, OfficialMarkers);
            var isRussian = ContainsAny(normalizedName, RussianMarkers)
                || ContainsCyrillic(originalName);
            var isTrailer = ContainsAny(normalizedName, TrailerMarkers);
            var isUnofficial = ContainsAny(normalizedName, UnofficialMarkers);

            if (isTrailer)
                score += 500;
            else if (isTeaser)
                score += 300;
            else if (isNonTrailer)
                score += 100;
            else
                score += 250;

            if (options.PreferOfficialTrailers && isOfficial)
                score += 200;

            if (options.PreferRussianTrailers && isRussian)
                score += 150;

            if (isUnofficial)
                score -= 400;

            var displayName = options.PrefixTrailerNames
                ? "КиноПоиск — " + originalName
                : originalName;

            return new TrailerCandidate(
                identity,
                canonicalUrl,
                displayName,
                item.Site,
                sourceKind,
                sourcePriority,
                score,
                index);
        }

        private static bool TryNormalizeSupportedUrl(
            VideoResponse_items item,
            out string identity,
            out string canonicalUrl,
            out int sourcePriority,
            out KinopoiskTrailerSourceKind sourceKind)
        {
            identity = null;
            canonicalUrl = null;
            sourcePriority = 0;
            sourceKind = KinopoiskTrailerSourceKind.Unknown;

            if (item is null || string.IsNullOrWhiteSpace(item.Url))
                return false;

            switch (item.Site)
            {
                case VideoResponse_itemsSite.YOUTUBE:
                    if (!TryNormalizeYoutubeUrl(item.Url, out var videoId, out canonicalUrl))
                        return false;

                    identity = "youtube:" + videoId;
                    sourcePriority = 1;
                    sourceKind = KinopoiskTrailerSourceKind.YouTube;
                    return true;

                case VideoResponse_itemsSite.KINOPOISK_WIDGET:
                    if (!TryNormalizeAllowedHttpsUrl(
                            item.Url,
                            KinopoiskWidgetHosts,
                            out canonicalUrl))
                    {
                        return false;
                    }

                    identity = "kinopoisk-widget:" + canonicalUrl;
                    sourcePriority = 0;
                    sourceKind = KinopoiskTrailerSourceKind.KinopoiskWidget;
                    return true;

                case VideoResponse_itemsSite.YANDEX_DISK:
                    if (!TryNormalizeAllowedHttpsUrl(
                            item.Url,
                            YandexDiskHosts,
                            out canonicalUrl))
                    {
                        return false;
                    }

                    identity = "yandex-disk:" + canonicalUrl;
                    sourcePriority = 2;
                    sourceKind = KinopoiskTrailerSourceKind.YandexDisk;
                    return true;

                default:
                    return false;
            }
        }

        private static bool TryNormalizeAllowedHttpsUrl(
            string value,
            IReadOnlyCollection<string> allowedHosts,
            out string normalizedUrl)
        {
            normalizedUrl = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                return false;
            }

            var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (!allowedHosts.Contains(host, StringComparer.OrdinalIgnoreCase))
                return false;

            var builder = new UriBuilder(uri)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1,
                Fragment = string.Empty
            };
            normalizedUrl = builder.Uri.AbsoluteUri;
            return true;
        }

        private static void RecordSelectionDiagnostics(
            IReadOnlyCollection<VideoResponse_items> sourceItems,
            IReadOnlyCollection<TrailerCandidate> selected)
        {
            var youtube = sourceItems.Count(item => item.Site == VideoResponse_itemsSite.YOUTUBE);
            var widget = sourceItems.Count(item => item.Site == VideoResponse_itemsSite.KINOPOISK_WIDGET);
            var yandex = sourceItems.Count(item => item.Site == VideoResponse_itemsSite.YANDEX_DISK);
            var unknown = sourceItems.Count - youtube - widget - yandex;

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Debug,
                typeof(KinopoiskTrailerSelector).FullName ?? nameof(KinopoiskTrailerSelector),
                "trailers.selection",
                "Завершён отбор трейлеров КиноПоиска.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["received"] = sourceItems.Count.ToString(CultureInfo.InvariantCulture),
                    ["selected"] = selected.Count.ToString(CultureInfo.InvariantCulture),
                    ["youtube"] = youtube.ToString(CultureInfo.InvariantCulture),
                    ["kinopoiskWidget"] = widget.ToString(CultureInfo.InvariantCulture),
                    ["yandexDisk"] = yandex.ToString(CultureInfo.InvariantCulture),
                    ["unknown"] = unknown.ToString(CultureInfo.InvariantCulture),
                    ["rejected"] = Math.Max(0, sourceItems.Count - selected.Count)
                        .ToString(CultureInfo.InvariantCulture)
                });
        }

        private static string GetFirstPathSegment(string path)
        {
            return path?
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .FirstOrDefault();
        }

        private static string GetQueryValue(string query, string requestedName)
        {
            if (string.IsNullOrWhiteSpace(query))
                return null;

            foreach (var pair in query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var separatorIndex = pair.IndexOf('=', StringComparison.Ordinal);
                var name = separatorIndex >= 0 ? pair[..separatorIndex] : pair;
                if (!string.Equals(
                    Uri.UnescapeDataString(name.Replace('+', ' ')),
                    requestedName,
                    StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var value = separatorIndex >= 0 ? pair[(separatorIndex + 1)..] : string.Empty;
                return Uri.UnescapeDataString(value.Replace('+', ' '));
            }

            return null;
        }

        private static bool ContainsAny(string value, IEnumerable<string> markers)
            => markers.Any(marker => value.Contains(marker, StringComparison.OrdinalIgnoreCase));

        private static bool ContainsCyrillic(string value)
            => value.Any(character => character is >= '\u0400' and <= '\u04FF');

        private static string NormalizeWhitespace(string value)
        {
            var builder = new StringBuilder(value.Length);
            var previousWasWhitespace = false;

            foreach (var character in value.Trim())
            {
                if (char.IsWhiteSpace(character))
                {
                    if (!previousWasWhitespace)
                        builder.Append(' ');
                    previousWasWhitespace = true;
                    continue;
                }

                builder.Append(character);
                previousWasWhitespace = false;
            }

            return builder.ToString();
        }

        [GeneratedRegex("^[A-Za-z0-9_-]{6,20}$", RegexOptions.CultureInvariant)]
        private static partial Regex YoutubeVideoIdRegex();

        private sealed record TrailerCandidate(
            string Identity,
            string CanonicalUrl,
            string DisplayName,
            VideoResponse_itemsSite Site,
            KinopoiskTrailerSourceKind SourceKind,
            int SourcePriority,
            int Score,
            int OriginalIndex);
    }

    internal enum KinopoiskTrailerSourceKind
    {
        Unknown,
        KinopoiskWidget,
        YouTube,
        YandexDisk
    }

    internal sealed record KinopoiskTrailerCandidate(
        string Url,
        string Name,
        KinopoiskTrailerSourceKind SourceKind);

    /// <summary>
    /// Содержит параметры отбора трейлеров КиноПоиска.
    /// </summary>
    public sealed class KinopoiskTrailerSelectionOptions
    {
        /// <summary>
        /// Получает или задаёт максимальное количество трейлеров.
        /// </summary>
        public int MaximumTrailers { get; set; } = 5;

        /// <summary>
        /// Получает или задаёт признак приоритета официальных роликов.
        /// </summary>
        public bool PreferOfficialTrailers { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак приоритета русскоязычных роликов.
        /// </summary>
        public bool PreferRussianTrailers { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак включения тизеров.
        /// </summary>
        public bool IncludeTeasers { get; set; } = true;

        /// <summary>
        /// Получает или задаёт признак включения фрагментов, интервью и дополнительных роликов.
        /// </summary>
        public bool IncludeAdditionalVideos { get; set; }

        /// <summary>
        /// Получает или задаёт признак добавления названия источника к заголовку ролика.
        /// </summary>
        public bool PrefixTrailerNames { get; set; } = true;
    }
}
