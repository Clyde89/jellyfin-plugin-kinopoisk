#nullable enable

using System;
using System.IO;
using System.IO.Pipelines;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http.Features;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлен буферизующий HTTP response-body feature с перехватом SendFileAsync.
    /// </summary>
    internal sealed class KinopoiskBufferedResponseBodyFeature : IHttpResponseBodyFeature
    {
        private const int CopyBufferSize = 64 * 1024;

        private readonly Stream _stream;
        private PipeWriter? _writer;

        public KinopoiskBufferedResponseBodyFeature(Stream stream)
        {
            _stream = stream ?? throw new ArgumentNullException(nameof(stream));
        }

        /// <inheritdoc />
        public Stream Stream => _stream;

        /// <inheritdoc />
        public PipeWriter Writer
            => _writer ??= PipeWriter.Create(
                _stream,
                new StreamPipeWriterOptions(leaveOpen: true));

        /// <inheritdoc />
        public void DisableBuffering()
        {
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        /// <inheritdoc />
        public async Task SendFileAsync(
            string path,
            long offset,
            long? count,
            CancellationToken cancellationToken = default)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(path);
            if (offset < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            if (count < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            await using var source = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                CopyBufferSize,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (offset > source.Length)
            {
                throw new ArgumentOutOfRangeException(nameof(offset));
            }

            source.Position = offset;
            if (!count.HasValue)
            {
                await source.CopyToAsync(
                    _stream,
                    CopyBufferSize,
                    cancellationToken).ConfigureAwait(false);
                return;
            }

            var remaining = count.Value;
            var buffer = new byte[CopyBufferSize];
            while (remaining > 0)
            {
                var requested = (int)Math.Min(buffer.Length, remaining);
                var read = await source.ReadAsync(
                    buffer.AsMemory(0, requested),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    throw new EndOfStreamException(
                        "Файл завершился раньше запрошенного диапазона SendFileAsync.");
                }

                await _stream.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken).ConfigureAwait(false);
                remaining -= read;
            }
        }

        /// <inheritdoc />
        public async Task CompleteAsync()
        {
            if (_writer is not null)
            {
                await _writer.FlushAsync().ConfigureAwait(false);
            }

            await _stream.FlushAsync().ConfigureAwait(false);
        }
    }
}
