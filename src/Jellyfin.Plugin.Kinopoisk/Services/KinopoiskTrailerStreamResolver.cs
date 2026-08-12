#nullable enable

using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    public interface IKinopoiskTrailerStreamResolver
    {
        Task<KinopoiskResolvedTrailerStream?> Resolve(
            string widgetUrl,
            CancellationToken cancellationToken);
    }

    public sealed class KinopoiskResolvedTrailerStream
    {
        public Uri WidgetUrl { get; init; } = null!;

        public Uri MediaUrl { get; init; } = null!;

        public string ContentType { get; init; } = "application/vnd.apple.mpegurl";
    }

    /// <summary>
    /// Разрешает страницу виджета КиноПоиска в проверенный HLS-манифест.
    /// </summary>
    public sealed class KinopoiskTrailerStreamResolver : IKinopoiskTrailerStreamResolver
    {
        public const string HttpClientName = "KinopoiskTrailerPlayback";

        private const int MaximumRedirects = 3;
        private const int MaximumWidgetBytes = 2 * 1024 * 1024;
        private const int MaximumManifestProbeBytes = 64 * 1024;
        private static readonly TimeSpan SuccessfulExpiration = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan EmptyExpiration = TimeSpan.FromMinutes(1);

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IMemoryCache _cache;
        private readonly ILogger<KinopoiskTrailerStreamResolver> _logger;

        public KinopoiskTrailerStreamResolver(
            IHttpClientFactory httpClientFactory,
            IMemoryCache cache,
            ILogger<KinopoiskTrailerStreamResolver> logger)
        {
            _httpClientFactory = httpClientFactory
                ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<KinopoiskResolvedTrailerStream?> Resolve(
            string widgetUrl,
            CancellationToken cancellationToken)
        {
            if (!KinopoiskTrailerUrlPolicy.TryNormalizeWidgetUrl(widgetUrl, out var normalizedWidget)
                || normalizedWidget is null)
            {
                throw new ArgumentException(
                    "Разрешены только HTTPS-ссылки официального виджета КиноПоиска.",
                    nameof(widgetUrl));
            }

            var cacheKey = "kinopoisk-trailer-stream:" + CreateStableHash(normalizedWidget.AbsoluteUri);
            if (_cache.TryGetValue(cacheKey, out CacheEntry? cached) && cached is not null)
                return cached.Stream;

            var result = await ResolveCore(normalizedWidget, cancellationToken)
                .ConfigureAwait(false);
            _cache.Set(
                cacheKey,
                new CacheEntry(result),
                result is null ? EmptyExpiration : SuccessfulExpiration);
            return result;
        }

        private async Task<KinopoiskResolvedTrailerStream?> ResolveCore(
            Uri widgetUri,
            CancellationToken cancellationToken)
        {
            var client = _httpClientFactory.CreateClient(HttpClientName);
            using var response = await SendWithValidatedRedirects(
                    client,
                    widgetUri,
                    widgetUri,
                    KinopoiskTrailerUrlPolicy.TryNormalizeWidgetUrl,
                    "text/html,application/xhtml+xml",
                    cancellationToken)
                .ConfigureAwait(false);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();
            var html = await ReadLimitedString(
                    response.Content,
                    MaximumWidgetBytes,
                    cancellationToken)
                .ConfigureAwait(false);
            var candidates = KinopoiskTrailerManifestParser.Extract(html);

            foreach (var candidate in candidates)
            {
                try
                {
                    var verified = await VerifyManifest(
                            client,
                            candidate,
                            widgetUri,
                            cancellationToken)
                        .ConfigureAwait(false);
                    if (verified is null)
                        continue;

                    return new KinopoiskResolvedTrailerStream
                    {
                        WidgetUrl = widgetUri,
                        MediaUrl = verified,
                        ContentType = "application/vnd.apple.mpegurl"
                    };
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    _logger.LogDebug(
                        exception,
                        "HLS-кандидат трейлера КиноПоиска на узле {Host} отклонён",
                        candidate.Host);
                }
            }

            return null;
        }

        private static async Task<Uri?> VerifyManifest(
            HttpClient client,
            Uri manifestUri,
            Uri widgetUri,
            CancellationToken cancellationToken)
        {
            using var response = await SendWithValidatedRedirects(
                    client,
                    manifestUri,
                    widgetUri,
                    KinopoiskTrailerUrlPolicy.TryNormalizeManifestUrl,
                    "application/vnd.apple.mpegurl,application/x-mpegURL,text/plain",
                    cancellationToken)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                return null;

            var prefix = await ReadLimitedString(
                    response.Content,
                    MaximumManifestProbeBytes,
                    cancellationToken,
                    allowTruncation: true)
                .ConfigureAwait(false);
            if (!prefix.TrimStart('\uFEFF', ' ', '\t', '\r', '\n')
                .StartsWith("#EXTM3U", StringComparison.Ordinal))
            {
                return null;
            }

            return response.RequestMessage?.RequestUri ?? manifestUri;
        }

        private static async Task<HttpResponseMessage> SendWithValidatedRedirects(
            HttpClient client,
            Uri initialUri,
            Uri referrer,
            TryNormalizeUrl tryNormalize,
            string accept,
            CancellationToken cancellationToken)
        {
            var currentUri = initialUri;
            for (var redirect = 0; redirect <= MaximumRedirects; redirect++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, currentUri);
                request.Headers.Accept.ParseAdd(accept);
                request.Headers.Referrer = referrer;
                request.Headers.TryAddWithoutValidation("Origin", "https://widgets.kinopoisk.ru");
                request.Headers.UserAgent.ParseAdd(
                    "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 "
                    + "(KHTML, like Gecko) Jellyfin-Kinopoisk/1.0");

                var response = await client.SendAsync(
                        request,
                        HttpCompletionOption.ResponseHeadersRead,
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!IsRedirect(response.StatusCode))
                    return response;

                var location = response.Headers.Location;
                response.Dispose();
                if (redirect == MaximumRedirects || location is null)
                    throw new HttpRequestException("Превышен предел перенаправлений трейлера КиноПоиска.");

                var redirectUri = location.IsAbsoluteUri
                    ? location
                    : new Uri(currentUri, location);
                if (!tryNormalize(redirectUri.AbsoluteUri, out var normalized)
                    || normalized is null)
                {
                    throw new HttpRequestException(
                        "Внешнее перенаправление трейлера КиноПоиска отклонено.");
                }

                currentUri = normalized;
            }

            throw new HttpRequestException("Превышен предел перенаправлений трейлера КиноПоиска.");
        }

        private static async Task<string> ReadLimitedString(
            HttpContent content,
            int maximumBytes,
            CancellationToken cancellationToken,
            bool allowTruncation = false)
        {
            if (content.Headers.ContentLength > maximumBytes && !allowTruncation)
                throw new InvalidDataException("Ответ виджета КиноПоиска превышает допустимый размер.");

            await using var source = await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
            using var buffer = new MemoryStream(Math.Min(maximumBytes, 64 * 1024));
            var chunk = new byte[16 * 1024];

            while (buffer.Length <= maximumBytes)
            {
                var remaining = maximumBytes - (int)buffer.Length;
                if (remaining == 0)
                {
                    if (allowTruncation)
                        break;

                    var extra = await source.ReadAsync(chunk.AsMemory(0, 1), cancellationToken)
                        .ConfigureAwait(false);
                    if (extra > 0)
                        throw new InvalidDataException("Ответ виджета КиноПоиска превышает допустимый размер.");
                    break;
                }

                var read = await source.ReadAsync(
                        chunk.AsMemory(0, Math.Min(chunk.Length, remaining)),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                    break;
                buffer.Write(chunk, 0, read);
            }

            return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
        }

        private static bool IsRedirect(HttpStatusCode statusCode)
            => statusCode is HttpStatusCode.MovedPermanently
                or HttpStatusCode.Redirect
                or HttpStatusCode.RedirectMethod
                or HttpStatusCode.TemporaryRedirect
                or HttpStatusCode.PermanentRedirect;

        private static string CreateStableHash(string value)
            => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

        private delegate bool TryNormalizeUrl(string value, out Uri? uri);

        private sealed record CacheEntry(KinopoiskResolvedTrailerStream? Stream);
    }
}
