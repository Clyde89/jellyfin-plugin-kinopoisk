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

        /// <summary>
        /// Регистрирует логический запрос к API.
        /// </summary>
        public void RecordApiRequest() => Interlocked.Increment(ref _apiRequests);

        /// <summary>
        /// Регистрирует ошибку логического запроса к API.
        /// </summary>
        public void RecordApiFailure() => Interlocked.Increment(ref _apiFailures);

        /// <summary>
        /// Регистрирует попадание в оперативный кэш.
        /// </summary>
        public void RecordMemoryCacheHit() => Interlocked.Increment(ref _memoryCacheHits);

        /// <summary>
        /// Регистрирует попадание в актуальный дисковый кэш.
        /// </summary>
        public void RecordPersistentCacheHit() => Interlocked.Increment(ref _persistentCacheHits);

        /// <summary>
        /// Регистрирует резервное использование устаревшего дискового кэша.
        /// </summary>
        public void RecordStaleCacheHit() => Interlocked.Increment(ref _staleCacheHits);

        /// <summary>
        /// Регистрирует запись ответа в дисковый кэш.
        /// </summary>
        public void RecordPersistentCacheWrite() => Interlocked.Increment(ref _persistentCacheWrites);

        /// <summary>
        /// Обновляет последнее подтверждённое состояние квоты.
        /// </summary>
        /// <param name="quota">Ответ API о состоянии ключа.</param>
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

        /// <summary>
        /// Регистрирует неудачную проверку состояния квоты.
        /// </summary>
        public void RecordQuotaFailure()
        {
            lock (_quotaSync)
                _lastQuotaFailureUtc = DateTimeOffset.UtcNow;
        }

        /// <summary>
        /// Возвращает согласованный снимок статистики.
        /// </summary>
        /// <returns>Снимок диагностики.</returns>
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
        public DateTimeOffset? LastQuotaCheckUtc { get; set; }
        public DateTimeOffset? LastQuotaFailureUtc { get; set; }
        public string AccountType { get; set; } = string.Empty;
        public long DailyQuotaValue { get; set; }
        public long DailyQuotaUsed { get; set; }
        public long TotalQuotaValue { get; set; }
        public long TotalQuotaUsed { get; set; }
    }
}
