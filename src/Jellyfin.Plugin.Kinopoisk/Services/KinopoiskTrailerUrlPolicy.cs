#nullable enable

using System;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Ограничивает внешние адреса, к которым сервер обращается при разрешении трейлера.
    /// </summary>
    internal static class KinopoiskTrailerUrlPolicy
    {
        internal static bool TryNormalizeWidgetUrl(string? value, out Uri? uri)
            => TryNormalize(
                value,
                host => host.Equals("widgets.kinopoisk.ru", StringComparison.Ordinal),
                out uri);

        internal static bool TryNormalizeManifestUrl(string? value, out Uri? uri)
            => TryNormalize(
                value,
                host => IsSameOrSubdomain(host, "kinopoisk.ru")
                    || IsSameOrSubdomain(host, "yandex.net")
                    || IsSameOrSubdomain(host, "yandex.ru")
                    || IsSameOrSubdomain(host, "yandex.com")
                    || IsSameOrSubdomain(host, "yandexcdn.net")
                    || IsSameOrSubdomain(host, "yastatic.net"),
                out uri);

        private static bool TryNormalize(
            string? value,
            Func<string, bool> isAllowedHost,
            out Uri? uri)
        {
            uri = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var candidate)
                || candidate.Scheme != Uri.UriSchemeHttps
                || candidate.HostNameType != UriHostNameType.Dns
                || !string.IsNullOrEmpty(candidate.UserInfo)
                || (!candidate.IsDefaultPort && candidate.Port != 443))
            {
                return false;
            }

            var host = candidate.IdnHost.TrimEnd('.').ToLowerInvariant();
            if (!isAllowedHost(host))
                return false;

            var builder = new UriBuilder(candidate)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = -1,
                Fragment = string.Empty
            };
            uri = builder.Uri;
            return true;
        }

        private static bool IsSameOrSubdomain(string host, string root)
            => host.Equals(root, StringComparison.Ordinal)
                || host.EndsWith('.' + root, StringComparison.Ordinal);
    }
}
