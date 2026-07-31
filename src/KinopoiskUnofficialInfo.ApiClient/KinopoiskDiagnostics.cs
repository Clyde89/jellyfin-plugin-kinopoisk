using System;
using System.Threading;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Собирает обезличенную статистику запросов, кэша и квоты.
    /// </summary>
    public sealed class KinopoiskDiagnostics
    {
        private readonly object _quotaSync = new();
        private long _apiRequests;
        private long _apiFailures;
        private long _memoryCacheHits;
        private long _persistentCacheHits;
        private long _staleCacheHits;
        private long _persistentCacheWrites;
        private long _imageCacheHits;
        private long _imageCacheMisses;
        private long _imageCacheStaleHits;
        private long _imageCacheWrites;
        private long _imageCacheBytesWritten;
        private DateTimeOffset? _lastQuotaCheckUtc;
        private DateTimeOffset? _lastQuotaFailureUtc;
        private string _accountType = string.Empty;
        private long _dailyQuotaValue;
        private long _dailyQuotaUsed;
        private long _totalQuotaValue;
        private long _totalQuotaUsed;

        /// <summary>
        /// Получает общий экземпляр диагностики текущего процесса Jellyfin.
        /// </summary>
        public static KinopoiskDiagnostics Shared { get; } = new();

        public void RecordApiRequest() => Interlocked.Increment(ref _apiRequests);

        public void RecordApiFailure() => Interlocked.Increment(ref _apiFailures);

        public void RecordMemoryCacheHit() => Interlocked.Increment(ref _memoryCacheHits);

        public void RecordPersistentCacheHit() => Interlocked.Increment(ref _persistentCacheHits);

        public void RecordStaleCacheHit() => Interlocked.Increment(ref _staleCacheHits);

        public void RecordPersistentCacheWrite() => Interlocked.Increment(ref _persistentCacheWrites);

        /// <summary>
        /// Регистрирует попадание в бинарный кэш изображений.
        /// </summary>
        public void RecordImageCacheHit() => Interlocked.Increment(ref _imageCacheHits);

        /// <summary>
        /// Регистрирует отсутствие изображения в локальном кэше.
        /// </summary>
        public void RecordImageCacheMiss() => Interlocked.Increment(ref _imageCacheMisses);

        /// <summary>
        /// Регистрирует резервное использование устаревшего изображения.
        /// </summary>
        public void RecordImageCacheStaleHit() => Interlocked.Increment(ref _imageCacheStaleHits);

        /// <summary>
        /// Регистрирует успешную запись бинарного изображения.
        /// </summary>
        /// <param name="bytes">Количество сохранённых байтов.</param>
        public void RecordImageCacheWrite(long bytes)
        {
            Interlocked.Increment(ref _imageCacheWrites);
            if (bytes > 0)
                Interlocked.Add(ref _imageCacheBytesWritten, bytes);
        }

        public void UpdateQuota(KinopoiskApiQuota quota)
        {
            ArgumentNullException.ThrowIfNull(quota);

            lock (_quotaSync)
            {
                _accountType = quota.AccountType ?? string.Empty;
                _dailyQuotaValue = quota.DailyQuota?.Value ?? 0;
                _dailyQuotaUsed = quota.DailyQuota?.Used ?? 0;
                _totalQuotaValue = quota.TotalQuota?.Value ?? 0;
                _totalQuotaUsed = quota.TotalQuota?.Used ?? 0;
                _lastQuotaCheckUtc = DateTimeOffset.UtcNow;
            }
        }

        public void RecordQuotaFailure()
        {
            lock (_quotaSync)
                _lastQuotaFailureUtc = DateTimeOffset.UtcNow;
        }

        public KinopoiskDiagnosticsSnapshot GetSnapshot()
        {
            lock (_quotaSync)
            {
                return new KinopoiskDiagnosticsSnapshot
                {
                    ApiRequests = Interlocked.Read(ref _apiRequests),
                    ApiFailures = Interlocked.Read(ref _apiFailures),
                    MemoryCacheHits = Interlocked.Read(ref _memoryCacheHits),
                    PersistentCacheHits = Interlocked.Read(ref _persistentCacheHits),
                    StaleCacheHits = Interlocked.Read(ref _staleCacheHits),
                    PersistentCacheWrites = Interlocked.Read(ref _persistentCacheWrites),
                    ImageCacheHits = Interlocked.Read(ref _imageCacheHits),
                    ImageCacheMisses = Interlocked.Read(ref _imageCacheMisses),
                    ImageCacheStaleHits = Interlocked.Read(ref _imageCacheStaleHits),
                    ImageCacheWrites = Interlocked.Read(ref _imageCacheWrites),
                    ImageCacheBytesWritten = Interlocked.Read(ref _imageCacheBytesWritten),
                    LastQuotaCheckUtc = _lastQuotaCheckUtc,
                    LastQuotaFailureUtc = _lastQuotaFailureUtc,
                    AccountType = _accountType,
                    DailyQuotaValue = _dailyQuotaValue,
                    DailyQuotaUsed = _dailyQuotaUsed,
                    TotalQuotaValue = _totalQuotaValue,
                    TotalQuotaUsed = _totalQuotaUsed
                };
            }
        }
    }

    /// <summary>
    /// Содержит неизменяемый снимок диагностики плагина.
    /// </summary>
    public sealed class KinopoiskDiagnosticsSnapshot
    {
        public long ApiRequests { get; set; }
        public long ApiFailures { get; set; }
        public long MemoryCacheHits { get; set; }
        public long PersistentCacheHits { get; set; }
        public long StaleCacheHits { get; set; }
        public long PersistentCacheWrites { get; set; }
        public long ImageCacheHits { get; set; }
        public long ImageCacheMisses { get; set; }
        public long ImageCacheStaleHits { get; set; }
        public long ImageCacheWrites { get; set; }
        public long ImageCacheBytesWritten { get; set; }
        public DateTimeOffset? LastQuotaCheckUtc { get; set; }
        public DateTimeOffset? LastQuotaFailureUtc { get; set; }
        public string AccountType { get; set; } = string.Empty;
        public long DailyQuotaValue { get; set; }
        public long DailyQuotaUsed { get; set; }
        public long TotalQuotaValue { get; set; }
        public long TotalQuotaUsed { get; set; }
    }
}
