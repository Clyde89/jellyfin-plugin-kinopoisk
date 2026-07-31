using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Сохраняет сериализованные ответы API в каталоге данных плагина.
    /// </summary>
    internal sealed class PersistentJsonCache
    {
        private readonly string _rootPath;
        private readonly long _maximumBytes;
        private readonly ILogger _logger;
        private readonly SemaphoreSlim _writeGate = new(1, 1);

        public PersistentJsonCache(string rootPath, long maximumBytes, ILogger logger)
        {
            if (string.IsNullOrWhiteSpace(rootPath))
                throw new ArgumentException("Каталог дискового кэша не должен быть пустым.", nameof(rootPath));

            _rootPath = rootPath;
            _maximumBytes = Math.Max(64L * 1024L * 1024L, maximumBytes);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            Directory.CreateDirectory(_rootPath);
        }

        public async Task<PersistentCacheReadResult<T>> Read<T>(
            string key,
            bool allowStale,
            CancellationToken cancellationToken)
        {
            var path = GetPath(key);
            if (!File.Exists(path))
                return PersistentCacheReadResult<T>.Miss;

            try
            {
                var json = await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
                var envelope = JsonConvert.DeserializeObject<PersistentCacheEnvelope<T>>(json);
                if (envelope is null)
                    return PersistentCacheReadResult<T>.Miss;

                var isStale = envelope.ExpiresAtUtc <= DateTimeOffset.UtcNow;
                if (isStale && !allowStale)
                    return PersistentCacheReadResult<T>.Miss;

                TryTouch(path);
                return new PersistentCacheReadResult<T>(
                    true,
                    isStale,
                    envelope.ExpiresAtUtc,
                    envelope.Value);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Повреждённая запись дискового кэша '{CacheFile}' проигнорирована",
                    Path.GetFileName(path));
                TryDelete(path);
                return PersistentCacheReadResult<T>.Miss;
            }
        }

        public async Task Write<T>(
            string key,
            T value,
            DateTimeOffset expiresAtUtc,
            CancellationToken cancellationToken)
        {
            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                Directory.CreateDirectory(_rootPath);
                var path = GetPath(key);
                var temporaryPath = path + ".tmp";
                var envelope = new PersistentCacheEnvelope<T>
                {
                    ExpiresAtUtc = expiresAtUtc,
                    Value = value
                };
                var json = JsonConvert.SerializeObject(envelope, Formatting.None);

                await File.WriteAllTextAsync(temporaryPath, json, Encoding.UTF8, cancellationToken)
                    .ConfigureAwait(false);
                File.Move(temporaryPath, path, true);
                TrimToMaximumSize();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Ответ API не сохранён в долговременный дисковый кэш");
            }
            finally
            {
                _writeGate.Release();
            }
        }

        private string GetPath(string key)
        {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
            return Path.Combine(_rootPath, Convert.ToHexString(hash) + ".json");
        }

        private void TrimToMaximumSize()
        {
            try
            {
                var files = new DirectoryInfo(_rootPath)
                    .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                    .OrderBy(file => file.LastAccessTimeUtc)
                    .ThenBy(file => file.LastWriteTimeUtc)
                    .ToArray();
                var totalBytes = files.Sum(file => file.Length);

                foreach (var file in files)
                {
                    if (totalBytes <= _maximumBytes)
                        break;

                    totalBytes -= file.Length;
                    file.Delete();
                }
            }
            catch (Exception exception)
            {
                _logger.LogDebug(exception, "Ограничение размера дискового кэша отложено до следующей записи");
            }
        }

        private static void TryTouch(string path)
        {
            try
            {
                File.SetLastAccessTimeUtc(path, DateTime.UtcNow);
            }
            catch
            {
                // Время последнего доступа используется только для необязательной ротации.
            }
        }

        private static void TryDelete(string path)
        {
            try
            {
                File.Delete(path);
            }
            catch
            {
                // Повреждённый файл будет повторно проигнорирован при следующем чтении.
            }
        }

        private sealed class PersistentCacheEnvelope<T>
        {
            public DateTimeOffset ExpiresAtUtc { get; set; }

            public T Value { get; set; }
        }
    }

    /// <summary>
    /// Содержит результат чтения долговременного кэша.
    /// </summary>
    internal readonly struct PersistentCacheReadResult<T>
    {
        public PersistentCacheReadResult(
            bool found,
            bool isStale,
            DateTimeOffset expiresAtUtc,
            T value)
        {
            Found = found;
            IsStale = isStale;
            ExpiresAtUtc = expiresAtUtc;
            Value = value;
        }

        public static PersistentCacheReadResult<T> Miss => default;

        public bool Found { get; }

        public bool IsStale { get; }

        public DateTimeOffset ExpiresAtUtc { get; }

        public T Value { get; }
    }
}
