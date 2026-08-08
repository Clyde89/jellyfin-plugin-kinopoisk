using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Кэширует бинарные файлы изображений, загружаемые поставщиками КиноПоиска.
    /// </summary>
    public sealed class KinopoiskImageBinaryCache
    {
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        };

        private readonly IHttpClientFactory _httpClientFactory;
        private readonly KinopoiskImageCacheOptions _options;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly ILogger<KinopoiskImageBinaryCache> _logger;
        private readonly ConcurrentDictionary<string, Lazy<Task<ImageRefreshResult>>> _inflight = new();
        private readonly SemaphoreSlim _trimGate = new(1, 1);

        /// <summary>
        /// Инициализирует новый экземпляр локального кэша изображений.
        /// </summary>
        public KinopoiskImageBinaryCache(
            IHttpClientFactory httpClientFactory,
            KinopoiskImageCacheOptions options,
            KinopoiskDiagnostics diagnostics,
            ILogger<KinopoiskImageBinaryCache> logger)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (_options.Enabled)
            {
                if (string.IsNullOrWhiteSpace(_options.CachePath))
                    throw new ArgumentException("Каталог бинарного кэша изображений не должен быть пустым.", nameof(options));

                Directory.CreateDirectory(_options.CachePath);
            }
        }

        /// <summary>
        /// Возвращает изображение из локального кэша либо загружает его с удалённого сервера.
        /// </summary>
        public async Task<HttpResponseMessage> GetImageResponse(
            string url,
            CancellationToken cancellationToken)
        {
            var uri = ValidateUrl(url);
            if (!_options.Enabled)
                return await SendDirect(uri, cancellationToken).ConfigureAwait(false);

            var key = GetCacheKey(uri.AbsoluteUri);
            var paths = GetPaths(key);
            var metadata = await ReadMetadata(paths.MetadataPath, cancellationToken).ConfigureAwait(false);

            if (IsFresh(metadata, paths.DataPath, uri.AbsoluteUri))
            {
                _diagnostics.RecordImageCacheHit();
                Touch(paths);
                return OpenCachedResponse(paths.DataPath, metadata);
            }

            _diagnostics.RecordImageCacheMiss();
            var sharedRefresh = _inflight.GetOrAdd(
                key,
                _ => new Lazy<Task<ImageRefreshResult>>(
                    () => RefreshAndRemove(key, uri, paths, metadata),
                    LazyThreadSafetyMode.ExecutionAndPublication));

            var refreshResult = await sharedRefresh.Value
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            if (refreshResult is ImageRefreshResult.Cached or ImageRefreshResult.Stale)
            {
                var refreshedMetadata = await ReadMetadata(paths.MetadataPath, cancellationToken)
                    .ConfigureAwait(false);
                if (refreshedMetadata is not null && File.Exists(paths.DataPath))
                {
                    Touch(paths);
                    return OpenCachedResponse(paths.DataPath, refreshedMetadata);
                }
            }

            return await SendDirect(uri, cancellationToken).ConfigureAwait(false);
        }

        private async Task<ImageRefreshResult> RefreshAndRemove(
            string key,
            Uri uri,
            CachePaths paths,
            ImageCacheMetadata existingMetadata)
        {
            try
            {
                return await Refresh(uri, paths, existingMetadata).ConfigureAwait(false);
            }
            finally
            {
                _inflight.TryRemove(key, out _);
            }
        }

        private async Task<ImageRefreshResult> Refresh(
            Uri uri,
            CachePaths paths,
            ImageCacheMetadata existingMetadata)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("image/*"));
                AddConditionalHeaders(request, existingMetadata, paths.DataPath);

                using var response = await _httpClientFactory
                    .CreateClient(NamedClient.Default)
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, CancellationToken.None)
                    .ConfigureAwait(false);

                if (response.StatusCode == HttpStatusCode.NotModified
                    && existingMetadata is not null
                    && File.Exists(paths.DataPath))
                {
                    existingMetadata.ExpiresAtUtc = DateTimeOffset.UtcNow + NormalizeExpiration();
                    await WriteMetadata(paths.MetadataPath, existingMetadata).ConfigureAwait(false);
                    _diagnostics.RecordImageCacheHit();
                    return ImageRefreshResult.Cached;
                }

                response.EnsureSuccessStatusCode();

                var contentType = response.Content.Headers.ContentType?.MediaType;
                if (string.IsNullOrWhiteSpace(contentType)
                    || !contentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogDebug(
                        "Ответ изображения '{Host}' не кэширован из-за типа содержимого '{ContentType}'",
                        uri.Host,
                        contentType);
                    return ImageRefreshResult.NotCached;
                }

                var declaredLength = response.Content.Headers.ContentLength;
                if (declaredLength.HasValue && declaredLength.Value > NormalizeMaximumFileBytes())
                {
                    _logger.LogWarning(
                        "Изображение '{Host}' размером {ImageBytes} байт не кэшировано: превышен лимит одного файла",
                        uri.Host,
                        declaredLength.Value);
                    return ImageRefreshResult.NotCached;
                }

                Directory.CreateDirectory(_options.CachePath);
                var temporaryDataPath = paths.DataPath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
                long writtenBytes;

                try
                {
                    await using var source = await response.Content.ReadAsStreamAsync(CancellationToken.None)
                        .ConfigureAwait(false);
                    await using var destination = new FileStream(
                        temporaryDataPath,
                        FileMode.CreateNew,
                        FileAccess.Write,
                        FileShare.None,
                        81920,
                        FileOptions.Asynchronous | FileOptions.SequentialScan);

                    writtenBytes = await CopyWithLimit(
                            source,
                            destination,
                            NormalizeMaximumFileBytes())
                        .ConfigureAwait(false);
                    await destination.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    TryDelete(temporaryDataPath);
                    throw;
                }

                var newMetadata = new ImageCacheMetadata
                {
                    SourceUrl = uri.AbsoluteUri,
                    ContentType = contentType,
                    ContentLength = writtenBytes,
                    EntityTag = response.Headers.ETag?.ToString(),
                    LastModifiedUtc = response.Content.Headers.LastModified,
                    ExpiresAtUtc = DateTimeOffset.UtcNow + NormalizeExpiration()
                };

                File.Move(temporaryDataPath, paths.DataPath, true);
                await WriteMetadata(paths.MetadataPath, newMetadata).ConfigureAwait(false);
                _diagnostics.RecordImageCacheWrite(writtenBytes);
                await TrimCache().ConfigureAwait(false);
                return ImageRefreshResult.Cached;
            }
            catch (ImageTooLargeException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Изображение '{Host}' не кэшировано: превышен лимит одного файла",
                    uri.Host);
                return ImageRefreshResult.NotCached;
            }
            catch (Exception exception)
            {
                if (_options.UseStaleOnFailure
                    && existingMetadata is not null
                    && File.Exists(paths.DataPath))
                {
                    _diagnostics.RecordImageCacheStaleHit();
                    _logger.LogWarning(
                        exception,
                        "Использовано устаревшее изображение из локального кэша для узла '{Host}'",
                        uri.Host);
                    return ImageRefreshResult.Stale;
                }

                throw;
            }
        }

        private async Task<HttpResponseMessage> SendDirect(Uri uri, CancellationToken cancellationToken)
        {
            return await _httpClientFactory
                .CreateClient(NamedClient.Default)
                .GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
        }

        private static Uri ValidateUrl(string url)
        {
            if (!Uri.TryCreate(url?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                throw new ArgumentException("URL изображения должен использовать HTTP или HTTPS.", nameof(url));
            }

            return uri;
        }

        private static void AddConditionalHeaders(
            HttpRequestMessage request,
            ImageCacheMetadata metadata,
            string dataPath)
        {
            if (metadata is null || !File.Exists(dataPath))
                return;

            if (EntityTagHeaderValue.TryParse(metadata.EntityTag, out var entityTag))
                request.Headers.IfNoneMatch.Add(entityTag);

            if (metadata.LastModifiedUtc.HasValue)
                request.Headers.IfModifiedSince = metadata.LastModifiedUtc;
        }

        private static bool IsFresh(
            ImageCacheMetadata metadata,
            string dataPath,
            string sourceUrl)
        {
            return metadata is not null
                && File.Exists(dataPath)
                && string.Equals(metadata.SourceUrl, sourceUrl, StringComparison.Ordinal)
                && metadata.ExpiresAtUtc > DateTimeOffset.UtcNow;
        }

        private static HttpResponseMessage OpenCachedResponse(
            string dataPath,
            ImageCacheMetadata metadata)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var stream = new FileStream(
                dataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                81920,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            response.Content = new StreamContent(stream);

            if (MediaTypeHeaderValue.TryParse(metadata.ContentType, out var contentType))
                response.Content.Headers.ContentType = contentType;

            response.Content.Headers.ContentLength = metadata.ContentLength > 0
                ? metadata.ContentLength
                : stream.Length;
            response.Content.Headers.LastModified = metadata.LastModifiedUtc;

            if (EntityTagHeaderValue.TryParse(metadata.EntityTag, out var entityTag))
                response.Headers.ETag = entityTag;

            response.Headers.TryAddWithoutValidation("X-Kinopoisk-Image-Cache", "HIT");
            return response;
        }

        private async Task<ImageCacheMetadata> ReadMetadata(
            string metadataPath,
            CancellationToken cancellationToken)
        {
            if (!File.Exists(metadataPath))
                return null;

            try
            {
                await using var stream = new FileStream(
                    metadataPath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan);
                return await JsonSerializer.DeserializeAsync<ImageCacheMetadata>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Повреждённые метаданные бинарного кэша изображений удалены");
                TryDelete(metadataPath);
                return null;
            }
        }

        private static async Task WriteMetadata(
            string metadataPath,
            ImageCacheMetadata metadata)
        {
            var temporaryPath = metadataPath + ".tmp-" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    4096,
                    FileOptions.Asynchronous | FileOptions.SequentialScan))
                {
                    await JsonSerializer.SerializeAsync(
                            stream,
                            metadata,
                            SerializerOptions,
                            CancellationToken.None)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(CancellationToken.None).ConfigureAwait(false);
                }

                File.Move(temporaryPath, metadataPath, true);
            }
            catch
            {
                TryDelete(temporaryPath);
                throw;
            }
        }

        private async Task TrimCache()
        {
            await _trimGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                if (!Directory.Exists(_options.CachePath))
                    return;

                var files = new DirectoryInfo(_options.CachePath)
                    .EnumerateFiles("*.img", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file.LastAccessTimeUtc)
                    .ThenBy(file => file.LastWriteTimeUtc)
                    .ToArray();
                var totalBytes = files.Sum(file => file.Length);
                var maximumBytes = NormalizeMaximumCacheBytes();

                foreach (var file in files)
                {
                    if (totalBytes <= maximumBytes)
                        break;

                    totalBytes -= file.Length;
                    var metadataPath = Path.ChangeExtension(file.FullName, ".json");
                    TryDelete(file.FullName);
                    TryDelete(metadataPath);
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Ограничение размера бинарного кэша изображений отложено до следующей записи");
            }
            finally
            {
                _trimGate.Release();
            }
        }

        private static async Task<long> CopyWithLimit(
            Stream source,
            Stream destination,
            long maximumBytes)
        {
            var buffer = new byte[81920];
            long totalBytes = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false);
                if (read == 0)
                    return totalBytes;

                totalBytes += read;
                if (totalBytes > maximumBytes)
                    throw new ImageTooLargeException(maximumBytes);

                await destination.WriteAsync(buffer.AsMemory(0, read), CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }

        private CachePaths GetPaths(string key)
        {
            return new CachePaths(
                Path.Combine(_options.CachePath, key + ".img"),
                Path.Combine(_options.CachePath, key + ".json"));
        }

        private static string GetCacheKey(string url)
        {
            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(url)));
        }

        private TimeSpan NormalizeExpiration()
            => _options.Expiration > TimeSpan.Zero ? _options.Expiration : TimeSpan.FromDays(30);

        private long NormalizeMaximumCacheBytes()
            => Math.Max(64L * 1024L * 1024L, _options.MaximumCacheBytes);

        private long NormalizeMaximumFileBytes()
            => Math.Max(1024L * 1024L, _options.MaximumFileBytes);

        private static void Touch(CachePaths paths)
        {
            try
            {
                var now = DateTime.UtcNow;
                File.SetLastAccessTimeUtc(paths.DataPath, now);
                File.SetLastAccessTimeUtc(paths.MetadataPath, now);
            }
            catch
            {
                // Время доступа используется только для необязательной ротации.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Файл будет повторно обработан при следующем обращении.
            }
        }

        private sealed class ImageCacheMetadata
        {
            public string SourceUrl { get; set; } = string.Empty;

            public string ContentType { get; set; } = string.Empty;

            public long ContentLength { get; set; }

            public string EntityTag { get; set; }

            public DateTimeOffset? LastModifiedUtc { get; set; }

            public DateTimeOffset ExpiresAtUtc { get; set; }
        }

        private readonly record struct CachePaths(string DataPath, string MetadataPath);

        private enum ImageRefreshResult
        {
            Cached,
            Stale,
            NotCached
        }

        private sealed class ImageTooLargeException : IOException
        {
            public ImageTooLargeException(long maximumBytes)
                : base($"Размер изображения превысил {maximumBytes.ToString(CultureInfo.InvariantCulture)} байт.")
            {
            }
        }
    }
}
