#nullable enable

using System;
using System.IO;
using System.Net.Http;
using System.Net.Mime;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Api
{
    /// <summary>
    /// Предоставлены разрешённые изображения карточки через локальный бинарный кэш.
    /// </summary>
    [ApiController]
    [Authorize]
    [Route("KinopoiskPresentation/image")]
    [Produces(MediaTypeNames.Application.Octet)]
    public sealed class KinopoiskPresentationImageController : ControllerBase
    {
        private readonly KinopoiskImageBinaryCache _imageBinaryCache;
        private readonly ILogger<KinopoiskPresentationImageController> _logger;

        public KinopoiskPresentationImageController(
            KinopoiskImageBinaryCache imageBinaryCache,
            ILogger<KinopoiskPresentationImageController> logger)
        {
            _imageBinaryCache = imageBinaryCache
                ?? throw new ArgumentNullException(nameof(imageBinaryCache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        [HttpGet]
        [ProducesResponseType(StatusCodes.Status200OK)]
        [ProducesResponseType(StatusCodes.Status400BadRequest)]
        [ProducesResponseType(StatusCodes.Status502BadGateway)]
        public async Task<IActionResult> GetImage(
            [FromQuery] string? url,
            CancellationToken cancellationToken)
        {
            if (!KinopoiskPresentationImageUrlValidator.TryNormalize(url, out var sourceUri))
                return BadRequest();

            HttpResponseMessage? upstream = null;
            try
            {
                upstream = await _imageBinaryCache
                    .GetImageResponse(sourceUri!.AbsoluteUri, cancellationToken)
                    .ConfigureAwait(false);
                if (!upstream.IsSuccessStatusCode)
                {
                    upstream.Dispose();
                    return Problem(
                        statusCode: StatusCodes.Status502BadGateway,
                        title: "Изображение КиноПоиска временно недоступно.");
                }

                var contentType = upstream.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType)
                    || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    upstream.Dispose();
                    return Problem(
                        statusCode: StatusCodes.Status502BadGateway,
                        title: "Источник вернул неподдерживаемый тип изображения.");
                }

                Response.Headers.CacheControl = "private, max-age=86400";
                Response.Headers.Vary = "Authorization";
                Response.Headers["X-Content-Type-Options"] = "nosniff";
                if (upstream.Headers.ETag is not null)
                    Response.Headers.ETag = upstream.Headers.ETag.ToString();
                if (upstream.Content.Headers.LastModified.HasValue)
                    Response.Headers.LastModified = upstream.Content.Headers.LastModified.Value.ToString("R");
                if (upstream.Content.Headers.ContentLength.HasValue)
                    Response.ContentLength = upstream.Content.Headers.ContentLength.Value;
                if (upstream.Headers.TryGetValues("X-Kinopoisk-Image-Cache", out var cacheValues))
                    Response.Headers["X-Kinopoisk-Image-Cache"] = string.Join(",", cacheValues);

                var stream = await upstream.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                return File(
                    new OwnedHttpResponseStream(stream, upstream),
                    contentType,
                    enableRangeProcessing: false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                upstream?.Dispose();
                throw;
            }
            catch (Exception exception)
            {
                upstream?.Dispose();
                _logger.LogWarning(
                    exception,
                    "Изображение карточки с узла {ImageHost} не предоставлено",
                    sourceUri!.Host);
                return Problem(
                    statusCode: StatusCodes.Status502BadGateway,
                    title: "Изображение КиноПоиска временно недоступно.");
            }
        }

        private sealed class OwnedHttpResponseStream : Stream
        {
            private readonly Stream _inner;
            private readonly HttpResponseMessage _owner;

            internal OwnedHttpResponseStream(Stream inner, HttpResponseMessage owner)
            {
                _inner = inner;
                _owner = owner;
            }

            public override bool CanRead => _inner.CanRead;
            public override bool CanSeek => _inner.CanSeek;
            public override bool CanWrite => false;
            public override long Length => _inner.Length;
            public override long Position
            {
                get => _inner.Position;
                set => _inner.Position = value;
            }

            public override void Flush() => _inner.Flush();
            public override int Read(byte[] buffer, int offset, int count)
                => _inner.Read(buffer, offset, count);
            public override long Seek(long offset, SeekOrigin origin)
                => _inner.Seek(offset, origin);
            public override void SetLength(long value)
                => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count)
                => throw new NotSupportedException();

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                {
                    _inner.Dispose();
                    _owner.Dispose();
                }
                base.Dispose(disposing);
            }

            public override async ValueTask DisposeAsync()
            {
                await _inner.DisposeAsync().ConfigureAwait(false);
                _owner.Dispose();
                GC.SuppressFinalize(this);
            }
        }
    }
}
