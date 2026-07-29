using System;
using System.Globalization;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Providers;

namespace Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers
{
    public static class VideoLookupInfoHelper
    {
        private static readonly Regex ProviderTagRegex = new(
            @"\s*[\[\{](?:tmdbid|tmdb|imdbid|imdb|tvdbid|tvdb|kp|kinopoiskid|kinopoisk)[-_:\s]?[^\]\}]+[\]\}]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private static readonly Regex KinopoiskIdRegex = new(
            @"kp-?(?<kinopoiskId>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        public static string GetSearchTitle(ItemLookupInfo info)
        {
            if (info is null)
                throw new ArgumentNullException(nameof(info));

            var title = info.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                return title;

            title = RemoveTrailingProviderTags(title);

            if (info.Year.HasValue)
            {
                var year = Regex.Escape(info.Year.Value.ToString(CultureInfo.InvariantCulture));
                title = Regex.Replace(
                    title,
                    $@"\s*[\(\[]\s*{year}\s*[\)\]]\s*$",
                    string.Empty,
                    RegexOptions.CultureInvariant).Trim();
            }

            return RemoveTrailingProviderTags(title);
        }

        public static bool TryGetKinopoiskIdFromPath(ItemLookupInfo info, out int kinopoiskId)
        {
            kinopoiskId = 0;

            if (info is null || string.IsNullOrWhiteSpace(info.Path))
                return false;

            var match = KinopoiskIdRegex.Match(info.Path);
            return match.Success
                && int.TryParse(
                    match.Groups["kinopoiskId"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out kinopoiskId);
        }

        public static bool IsExactTitle(string targetTitle, string candidateTitle)
        {
            return !string.IsNullOrWhiteSpace(targetTitle)
                && !string.IsNullOrWhiteSpace(candidateTitle)
                && string.Equals(
                    targetTitle.Trim(),
                    candidateTitle.Trim(),
                    StringComparison.OrdinalIgnoreCase);
        }

        private static string RemoveTrailingProviderTags(string title)
        {
            var result = title;

            while (true)
            {
                var cleaned = ProviderTagRegex.Replace(result, string.Empty).Trim();
                if (string.Equals(cleaned, result, StringComparison.Ordinal))
                    return result;

                result = cleaned;
            }
        }
    }
}
