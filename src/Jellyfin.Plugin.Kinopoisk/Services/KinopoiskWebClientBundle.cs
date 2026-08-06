#nullable enable

using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Собирает автономный клиентский bundle из встроенных ресурсов плагина.
    /// </summary>
    internal static class KinopoiskWebClientBundle
    {
        private static readonly string[] ResourceNames =
        {
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskWidgetTrailerPlayer.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskEnhancedPresentation.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskReviewsIntegration.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskTagLocalization.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskElsewhereBridge.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendations.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationSeerrFallback.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimePolish.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskCarouselRebind.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeUiCorrections.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeNativeStyle.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationLifecycleGuard.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationScrollbarStyle.js"
        };

        private static readonly Lazy<BundleData> Bundle = new(
            CreateBundle,
            isThreadSafe: true);

        /// <summary>
        /// Получает объединённое содержимое клиентских модулей.
        /// </summary>
        internal static string Content => Bundle.Value.Content;

        /// <summary>
        /// Получает UTF-8 представление клиентского bundle.
        /// </summary>
        internal static byte[] Bytes => Bundle.Value.Bytes;

        /// <summary>
        /// Получает SHA-256 клиентского bundle.
        /// </summary>
        internal static string Sha256 => Bundle.Value.Sha256;

        /// <summary>
        /// Получает короткий токен версии для URL клиентского bundle.
        /// </summary>
        internal static string VersionToken => Sha256[..16];

        /// <summary>
        /// Получает количество включённых клиентских модулей.
        /// </summary>
        internal static int ResourceCount => ResourceNames.Length;

        private static BundleData CreateBundle()
        {
            var assembly = typeof(KinopoiskWebClientBundle).Assembly;
            var builder = new StringBuilder();
            foreach (var resourceName in ResourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException(
                        $"Встроенный ресурс веб-клиента не найден: {resourceName}");
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                if (builder.Length > 0)
                    builder.AppendLine().AppendLine();

                builder.Append(reader.ReadToEnd());
            }

            var content = builder.ToString();
            var bytes = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
                .GetBytes(content);
            var sha256 = Convert.ToHexString(SHA256.HashData(bytes))
                .ToLowerInvariant();
            return new BundleData(content, bytes, sha256);
        }

        private sealed record BundleData(string Content, byte[] Bytes, string Sha256);
    }
}
