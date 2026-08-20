#nullable enable

using System;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Хранит готовые MP4-трейлеры с атомарной записью и LRU-ротацией.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCache : IKinopoiskNativeTrailerCache
    {
        private const long MinimumValidFileBytes = 64L * 1024L;
        private static readonly JsonSerializerOptions SerializerOptions = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true
        };

        private readonly KinopoiskNativeTrailerCacheOptions _options;
        private readonly KinopoiskNativeTrailerRemuxer _remuxer;
        private readonly ILogger<KinopoiskNativeTrailerCache> _logger;
        private readonly ConcurrentDictionary<int, SemaphoreSlim> _entryGates = new();
        private readonly ConcurrentDictionary<int, DateTimeOffset> _lastTouches = new();
        private readonly SemaphoreSlim _cleanupGate = new(1, 1);

        public KinopoiskNativeTrailerCache(
            KinopoiskNativeTrailerCacheOptions options,
            KinopoiskNativeTrailerRemuxer remuxer,
            ILogger<KinopoiskNativeTrailerCache> logger)
        {
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _remuxer = remuxer ?? throw new ArgumentNullException(nameof(remuxer));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));

            if (Enabled)
            {
                if (string.IsNullOrWhiteSpace(_options.CachePath))
                    throw new ArgumentException("Каталог локального кэша трейлеров не должен быть пустым.", nameof(options));
                Directory.CreateDirectory(_options.CachePath);
            }
        }

        public bool Enabled => _options.Enabled;

        public string GetExpectedPath(int kinopoiskId)
            => Path.Combine(
                _options.CachePath,
                $"kinopoisk-{kinopoiskId.ToString(CultureInfo.InvariantCulture)}.mp4");

        public KinopoiskNativeTrailerCacheEntry? TryGet(int kinopoiskId, bool touch = true)
        {
            if (!Enabled)
                return null;

            var paths = GetPaths(kinopoiskId);
            var entry = ReadMetadata(paths.MetadataPath);
            if (entry is null
                || entry.KinopoiskId != kinopoiskId
                || !File.Exists(paths.DataPath))
            {
                return null;
            }

            var file = new FileInfo(paths.DataPath);
            if (file.Length < MinimumValidFileBytes
                || file.Length != entry.ContentLength)
            {
                return null;
            }

            entry.Path = paths.DataPath;
            entry.LastAccessUtc = new DateTimeOffset(file.LastAccessTimeUtc, TimeSpan.Zero);
            if (touch)
                Touch(kinopoiskId, paths, entry);
            return entry;
        }

        public async Task<KinopoiskNativeTrailerCacheEntry?> GetOrCreate(
            int kinopoiskId,
            KinopoiskTrailerPlaybackSource source,
            CancellationToken cancellationToken)
        {
            if (!Enabled)
                return null;

            var cached = TryGet(kinopoiskId);
            if (cached is not null)
                return cached;

            var entryGate = _entryGates.GetOrAdd(kinopoiskId, _ => new SemaphoreSlim(1, 1));
            await entryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cached = TryGet(kinopoiskId);
                if (cached is not null)
                    return cached;

                Directory.CreateDirectory(_options.CachePath);
                var paths = GetPaths(kinopoiskId);
                var temporaryDataPath = paths.DataPath
                    + ".partial-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture)
                    + ".mp4";
                var temporaryMetadataPath = paths.MetadataPath
                    + ".partial-"
                    + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);

                try
                {
                    var created = await _remuxer
                        .Remux(kinopoiskId, source, temporaryDataPath, cancellationToken)
                        .ConfigureAwait(false);
                    created.Path = paths.DataPath;
                    created.ContentLength = new FileInfo(temporaryDataPath).Length;

                    await WriteMetadata(temporaryMetadataPath, created, cancellationToken)
                        .ConfigureAwait(false);
                    File.Move(temporaryDataPath, paths.DataPath, true);
                    File.Move(temporaryMetadataPath, paths.MetadataPath, true);
                    Touch(kinopoiskId, paths, created, true);
                    await CleanupInternal(paths.DataPath, cancellationToken).ConfigureAwait(false);
                    return TryGet(kinopoiskId);
                }
                catch
                {
                    TryDelete(temporaryDataPath);
                    TryDelete(temporaryMetadataPath);
                    throw;
                }
            }
            finally
            {
                entryGate.Release();
            }
        }

        public Task<KinopoiskNativeTrailerCacheCleanupResult> Cleanup(
            CancellationToken cancellationToken)
            => CleanupInternal(null, cancellationToken);

        public KinopoiskNativeTrailerCacheSnapshot GetSnapshot()
        {
            if (!Enabled || !Directory.Exists(_options.CachePath))
            {
                return new KinopoiskNativeTrailerCacheSnapshot
                {
                    Enabled = Enabled,
                    MaximumBytes = NormalizeMaximumBytes(),
                    RetentionDays = (int)Math.Ceiling(NormalizeExpiration().TotalDays)
                };
            }

            try
            {
                var files = new DirectoryInfo(_options.CachePath)
                    .EnumerateFiles("kinopoisk-*.mp4", SearchOption.TopDirectoryOnly)
                    .ToArray();
                return new KinopoiskNativeTrailerCacheSnapshot
                {
                    Enabled = true,
                    MaximumBytes = NormalizeMaximumBytes(),
                    RetentionDays = (int)Math.Ceiling(NormalizeExpiration().TotalDays),
                    CurrentBytes = files.Sum(file => file.Length),
                    FileCount = files.Length
                };
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Не удалось сформировать снимок локального кэша трейлеров");
                return new KinopoiskNativeTrailerCacheSnapshot
                {
                    Enabled = true,
                    MaximumBytes = NormalizeMaximumBytes(),
                    RetentionDays = (int)Math.Ceiling(NormalizeExpiration().TotalDays)
                };
            }
        }

        private async Task<KinopoiskNativeTrailerCacheCleanupResult> CleanupInternal(
            string? protectedPath,
            CancellationToken cancellationToken)
        {
            var result = new KinopoiskNativeTrailerCacheCleanupResult();
            if (!Enabled || !Directory.Exists(_options.CachePath))
                return result;

            await _cleanupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                var directory = new DirectoryInfo(_options.CachePath);
                var temporaryCutoff = DateTime.UtcNow.AddDays(-1);
                foreach (var temporary in directory.EnumerateFiles("*.partial-*", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (temporary.LastWriteTimeUtc >= temporaryCutoff)
                        continue;
                    if (TryDelete(temporary.FullName))
                        result.RemovedTemporaryFiles++;
                }

                foreach (var metadata in directory.EnumerateFiles("kinopoisk-*.json", SearchOption.TopDirectoryOnly))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (File.Exists(Path.ChangeExtension(metadata.FullName, ".mp4")))
                        continue;
                    if (TryDelete(metadata.FullName))
                        result.RemovedTemporaryFiles++;
                }

                var expirationCutoff = DateTime.UtcNow - NormalizeExpiration();
                var files = directory
                    .EnumerateFiles("kinopoisk-*.mp4", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file.LastAccessTimeUtc)
                    .ThenBy(file => file.LastWriteTimeUtc)
                    .ToList();

                foreach (var file in files.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (IsProtected(file.FullName, protectedPath)
                        || file.LastAccessTimeUtc >= expirationCutoff)
                    {
                        continue;
                    }

                    if (DeleteEntry(file, out var removedBytes))
                    {
                        result.RemovedExpiredFiles++;
                        result.RemovedBytes += removedBytes;
                        files.Remove(file);
                    }
                }

                var totalBytes = files.Sum(file => file.Exists ? file.Length : 0L);
                var maximumBytes = NormalizeMaximumBytes();
                foreach (var file in files.ToArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (totalBytes <= maximumBytes)
                        break;
                    if (IsProtected(file.FullName, protectedPath))
                        continue;

                    if (DeleteEntry(file, out var removedBytes))
                    {
                        result.RemovedCapacityFiles++;
                        result.RemovedBytes += removedBytes;
                        totalBytes -= removedBytes;
                        files.Remove(file);
                    }
                }

                result.RemainingBytes = Math.Max(0, totalBytes);
                result.RemainingFiles = files.Count(file => file.Exists);
                return result;
            }
            finally
            {
                _cleanupGate.Release();
            }
        }

        private bool DeleteEntry(FileInfo file, out long removedBytes)
        {
            removedBytes = file.Exists ? file.Length : 0;
            if (!TryDelete(file.FullName))
                return false;

            TryDelete(Path.ChangeExtension(file.FullName, ".json"));
            return true;
        }

        private void Touch(
            int kinopoiskId,
            CachePaths paths,
            KinopoiskNativeTrailerCacheEntry entry,
            bool force = false)
        {
            var now = DateTimeOffset.UtcNow;
            if (!force
                && _lastTouches.TryGetValue(kinopoiskId, out var previous)
                && now - previous < TimeSpan.FromMinutes(10))
            {
                return;
            }

            try
            {
                var utc = now.UtcDateTime;
                File.SetLastAccessTimeUtc(paths.DataPath, utc);
                File.SetLastAccessTimeUtc(paths.MetadataPath, utc);
                entry.LastAccessUtc = now;
                _lastTouches[kinopoiskId] = now;
            }
            catch (Exception exception)
            {
                _logger.LogDebug(
                    exception,
                    "Не удалось обновить LRU-время локального трейлера Kinopoisk ID {KinopoiskId}",
                    kinopoiskId);
            }
        }

        private static async Task WriteMetadata(
            string path,
            KinopoiskNativeTrailerCacheEntry entry,
            CancellationToken cancellationToken)
        {
            await using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await JsonSerializer.SerializeAsync(
                    stream,
                    entry,
                    SerializerOptions,
                    cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        private KinopoiskNativeTrailerCacheEntry? ReadMetadata(string path)
        {
            if (!File.Exists(path))
                return null;

            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite,
                    4096,
                    FileOptions.SequentialScan);
                return JsonSerializer.Deserialize<KinopoiskNativeTrailerCacheEntry>(
                    stream,
                    SerializerOptions);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Повреждённые метаданные локального трейлера удалены");
                TryDelete(path);
                return null;
            }
        }

        private CachePaths GetPaths(int kinopoiskId)
        {
            var dataPath = GetExpectedPath(kinopoiskId);
            return new CachePaths(dataPath, Path.ChangeExtension(dataPath, ".json"));
        }

        private long NormalizeMaximumBytes()
            => Math.Clamp(
                _options.MaximumCacheBytes,
                256L * 1024L * 1024L,
                16L * 1024L * 1024L * 1024L);

        private TimeSpan NormalizeExpiration()
            => TimeSpan.FromDays(Math.Clamp(
                _options.UnusedExpiration.TotalDays,
                7,
                365));

        private static bool IsProtected(string path, string? protectedPath)
            => !string.IsNullOrWhiteSpace(protectedPath)
                && string.Equals(
                    Path.GetFullPath(path),
                    Path.GetFullPath(protectedPath),
                    StringComparison.Ordinal);

        private static bool TryDelete(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return true;
                File.Delete(path);
                return true;
            }
            catch
            {
                return false;
            }
        }

        private readonly record struct CachePaths(string DataPath, string MetadataPath);
    }
}
