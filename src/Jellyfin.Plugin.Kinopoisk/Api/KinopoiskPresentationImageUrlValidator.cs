#nullable enable

using System;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Проверен внешний адрес изображения перед передачей в серверный кэш.
    /// </summary>
    internal static class KinopoiskPresentationImageUrlValidator
    {
        internal static bool TryNormalize(string? value, out Uri? uri)
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

            var host = candidate.IdnHost.ToLowerInvariant();
            if (!IsAllowedHost(host))
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

        private static bool IsAllowedHost(string host)
            => host.Equals("kinopoiskapiunofficial.tech", StringComparison.Ordinal)
                || IsSameOrSubdomain(host, "yandex.net")
                || IsSameOrSubdomain(host, "kinopoisk.ru")
                || IsSameOrSubdomain(host, "kp.yandex.net");

        private static bool IsSameOrSubdomain(string host, string root)
            => host.Equals(root, StringComparison.Ordinal)
                || host.EndsWith('.' + root, StringComparison.Ordinal);
    }
}
