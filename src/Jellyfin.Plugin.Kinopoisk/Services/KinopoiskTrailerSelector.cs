using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Model.Entities;

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

        /// <summary>
        /// Возвращает поддерживаемые Jellyfin трейлеры в приоритетном порядке.
        /// </summary>
        /// <param name="response">Ответ API КиноПоиска.</param>
        /// <param name="options">Параметры отбора.</param>
        /// <returns>Канонические YouTube-ссылки для стандартного плеера Jellyfin.</returns>
        public static IReadOnlyList<MediaUrl> Select(
            VideoResponse response,
            KinopoiskTrailerSelectionOptions options)
        {
            options ??= new KinopoiskTrailerSelectionOptions();
            var maximumTrailers = Math.Clamp(options.MaximumTrailers, 1, 20);

            if (response?.Items is null || response.Items.Count == 0)
                return Array.Empty<MediaUrl>();

            return response.Items
                .Select((item, index) => CreateCandidate(item, index, options))
                .Where(candidate => candidate is not null)
                .GroupBy(candidate => candidate.VideoId, StringComparer.Ordinal)
                .Select(group => group
                    .OrderByDescending(candidate => candidate.Score)
                    .ThenBy(candidate => candidate.OriginalIndex)
                    .First())
                .OrderByDescending(candidate => candidate.Score)
                .ThenBy(candidate => candidate.OriginalIndex)
                .Take(maximumTrailers)
                .Select(candidate => new MediaUrl
                {
                    Name = candidate.DisplayName,
                    Url = candidate.CanonicalUrl
                })
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
            if (item is null
                || item.Site != VideoResponse_itemsSite.YOUTUBE
                || !TryNormalizeYoutubeUrl(item.Url, out var videoId, out var canonicalUrl))
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

            var score = 0;
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
                videoId,
                canonicalUrl,
                displayName,
                score,
                index);
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
            string VideoId,
            string CanonicalUrl,
            string DisplayName,
            int Score,
            int OriginalIndex);
    }

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
