#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Primitives;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлено недеструктивное подключение WebClient.js к HTML-ответу Jellyfin Web.
    /// </summary>
    internal sealed class KinopoiskWebBootstrapMiddleware
    {
        private const long MaximumIndexBytes = 2L * 1024L * 1024L;
        private const string AcceptEncodingHeader = "Accept-Encoding";
        private const string IfNoneMatchHeader = "If-None-Match";
        private const string IfModifiedSinceHeader = "If-Modified-Since";
        private const string ContentEncodingHeader = "Content-Encoding";
        private const string BootstrapHeader = "X-Kinopoisk-Web-Bootstrap";

        private readonly RequestDelegate _next;
        private readonly KinopoiskWebBootstrapState _state;
        private readonly ILogger<KinopoiskWebBootstrapMiddleware> _logger;

        public KinopoiskWebBootstrapMiddleware(
            RequestDelegate next,
            KinopoiskWebBootstrapState state,
            ILogger<KinopoiskWebBootstrapMiddleware> logger)
        {
            _next = next ?? throw new ArgumentNullException(nameof(next));
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Выполнено преобразование только главного HTML Jellyfin Web.
        /// </summary>
        /// <param name="context">Текущий HTTP-контекст.</param>
        /// <returns>Асинхронная операция.</returns>
        public async Task InvokeAsync(HttpContext context)
        {
            if (!ShouldTransform(context.Request))
            {
                await _next(context).ConfigureAwait(false);
                return;
            }

            var acceptEncoding = CaptureHeader(context.Request.Headers, AcceptEncodingHeader);
            var ifNoneMatch = CaptureHeader(context.Request.Headers, IfNoneMatchHeader);
            var ifModifiedSince = CaptureHeader(context.Request.Headers, IfModifiedSinceHeader);

            context.Request.Headers.Remove(AcceptEncodingHeader);
            context.Request.Headers.Remove(IfNoneMatchHeader);
            context.Request.Headers.Remove(IfModifiedSinceHeader);

            var originalBody = context.Response.Body;
            await using var capturedBody = new MemoryStream();
            context.Response.Body = capturedBody;

            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch
            {
                RestoreRequestHeaders(
                    context,
                    acceptEncoding,
                    ifNoneMatch,
                    ifModifiedSince);
                context.Response.Body = originalBody;
                throw;
            }

            RestoreRequestHeaders(
                context,
                acceptEncoding,
                ifNoneMatch,
                ifModifiedSince);
            context.Response.Body = originalBody;

            if (!CanTransformResponse(context.Response, capturedBody.Length))
            {
                await CopyCapturedBodyAsync(capturedBody, originalBody, context).ConfigureAwait(false);
                return;
            }

            try
            {
                var sourceBytes = capturedBody.ToArray();
                var transformedBytes = KinopoiskWebBootstrapTransformer.TransformUtf8(sourceBytes);
                var etag = KinopoiskWebBootstrapTransformer.ComputeEtag(transformedBytes);

                context.Response.Headers.ETag = etag;
                context.Response.Headers.CacheControl = "no-cache";
                context.Response.Headers[BootstrapHeader] = "runtime";
                context.Response.Headers.Remove(ContentEncodingHeader);

                if (MatchesIfNoneMatch(ifNoneMatch, etag))
                {
                    context.Response.StatusCode = StatusCodes.Status304NotModified;
                    context.Response.ContentLength = null;
                    _state.RecordSuccess(etag);
                    return;
                }

                context.Response.ContentLength = transformedBytes.LongLength;
                await originalBody.WriteAsync(
                    transformedBytes,
                    context.RequestAborted).ConfigureAwait(false);
                _state.RecordSuccess(etag);
            }
            catch (Exception exception) when (exception is InvalidDataException
                or DecoderFallbackException
                or EncoderFallbackException)
            {
                _state.RecordFailure();
                _logger.LogWarning(
                    exception,
                    "Runtime Web Bootstrap КиноПоиска не преобразовал index.html; возвращён исходный ответ Jellyfin.");
                await CopyCapturedBodyAsync(capturedBody, originalBody, context).ConfigureAwait(false);
            }
        }

        internal static bool ShouldTransform(HttpRequest request)
        {
            if (!HttpMethods.IsGet(request.Method))
            {
                return false;
            }

            var path = request.Path.Value;
            return !string.IsNullOrEmpty(path)
                && (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase)
                    || path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase));
        }

        internal static bool MatchesIfNoneMatch(HeaderSnapshot snapshot, string etag)
        {
            if (!snapshot.Exists)
            {
                return false;
            }

            var normalizedExpected = NormalizeEtag(etag);
            foreach (var headerValue in snapshot.Value)
            {
                if (string.IsNullOrWhiteSpace(headerValue))
                {
                    continue;
                }

                foreach (var token in headerValue.Split(',', StringSplitOptions.TrimEntries))
                {
                    if (string.Equals(token, "*", StringComparison.Ordinal))
                    {
                        return true;
                    }

                    if (string.Equals(
                        NormalizeEtag(token),
                        normalizedExpected,
                        StringComparison.Ordinal))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        private static bool CanTransformResponse(HttpResponse response, long bodyLength)
        {
            if (response.StatusCode != StatusCodes.Status200OK
                || bodyLength <= 0
                || bodyLength > MaximumIndexBytes)
            {
                return false;
            }

            if (response.Headers.TryGetValue(ContentEncodingHeader, out var encoding)
                && !StringValues.IsNullOrEmpty(encoding))
            {
                return false;
            }

            return response.ContentType?.StartsWith(
                "text/html",
                StringComparison.OrdinalIgnoreCase) == true;
        }

        private static async Task CopyCapturedBodyAsync(
            MemoryStream capturedBody,
            Stream target,
            HttpContext context)
        {
            capturedBody.Position = 0;
            await capturedBody.CopyToAsync(target, context.RequestAborted).ConfigureAwait(false);
        }

        private static HeaderSnapshot CaptureHeader(IHeaderDictionary headers, string name)
            => headers.TryGetValue(name, out var value)
                ? new HeaderSnapshot(true, value)
                : new HeaderSnapshot(false, default);

        private static void RestoreRequestHeaders(
            HttpContext context,
            HeaderSnapshot acceptEncoding,
            HeaderSnapshot ifNoneMatch,
            HeaderSnapshot ifModifiedSince)
        {
            RestoreHeader(context.Request.Headers, AcceptEncodingHeader, acceptEncoding);
            RestoreHeader(context.Request.Headers, IfNoneMatchHeader, ifNoneMatch);
            RestoreHeader(context.Request.Headers, IfModifiedSinceHeader, ifModifiedSince);
        }

        private static void RestoreHeader(
            IHeaderDictionary headers,
            string name,
            HeaderSnapshot snapshot)
        {
            if (snapshot.Exists)
            {
                headers[name] = snapshot.Value;
            }
            else
            {
                headers.Remove(name);
            }
        }

        private static string NormalizeEtag(string value)
        {
            var normalized = value.Trim();
            if (normalized.StartsWith("W/", StringComparison.OrdinalIgnoreCase))
            {
                normalized = normalized[2..].TrimStart();
            }

            return normalized;
        }

        internal readonly record struct HeaderSnapshot(bool Exists, StringValues Value);
    }
}
