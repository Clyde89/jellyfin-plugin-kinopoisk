using System;
using System.Collections.Generic;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public sealed class KinopoiskDiagnostics
    {
        private readonly object _quotaSync = new();
        private readonly object _diagnosticSinkSync = new();
        private IKinopoiskDiagnosticSink _diagnosticSink;
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

        public static KinopoiskDiagnostics Shared { get; } = new();

        public void AttachDiagnosticSink(IKinopoiskDiagnosticSink diagnosticSink)
        {
            ArgumentNullException.ThrowIfNull(diagnosticSink);

            lock (_diagnosticSinkSync)
                _diagnosticSink = diagnosticSink;
        }

        public void StartDiagnosticSession()
        {
            var sink = GetDiagnosticSink();
            if (sink is null)
                return;

            try
            {
                sink.BeginSession();
                sink.Write(new KinopoiskDiagnosticEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Level = LogLevel.Information,
                    Category = typeof(KinopoiskDiagnostics).FullName ?? nameof(KinopoiskDiagnostics),
                    EventName = "diagnostic.session.started",
                    Message = "Диагностическая сессия КиноПоиска начата прямым каналом событий."
                });
            }
            catch
            {
            }
        }

        public void StopDiagnosticSession()
        {
            RecordDiagnosticEvent(
                LogLevel.Information,
                typeof(KinopoiskDiagnostics).FullName ?? nameof(KinopoiskDiagnostics),
                "diagnostic.session.stopped",
                "Диагностическая сессия КиноПоиска остановлена.");
        }

        public void RecordDiagnosticEvent(
            LogLevel level,
            string category,
            string eventName,
            string message,
            IReadOnlyDictionary<string, string> fields = null)
        {
            var sink = GetDiagnosticSink();
            if (sink is null)
                return;

            try
            {
                sink.Write(new KinopoiskDiagnosticEvent
                {
                    TimestampUtc = DateTimeOffset.UtcNow,
                    Level = level,
                    Category = category ?? string.Empty,
                    EventName = eventName ?? string.Empty,
                    Message = message ?? string.Empty,
                    Fields = fields
                });
            }
            catch
            {
            }
        }

        public void RecordApiRequest()
        {
            var count = Interlocked.Increment(ref _apiRequests);
            RecordDiagnosticEvent(
                LogLevel.Debug,
                "KinopoiskUnofficialInfo.ApiClient",
                "api.request",
                "Зарегистрирован запрос к API КиноПоиска.",
                CreateCountFields(count));
        }

        public void RecordApiFailure()
        {
            var count = Interlocked.Increment(ref _apiFailures);
            RecordDiagnosticEvent(
                LogLevel.Error,
                "KinopoiskUnofficialInfo.ApiClient",
                "api.failure",
                "Зарегистрирована ошибка запроса к API КиноПоиска.",
                CreateCountFields(count));
        }

        public void RecordMemoryCacheHit()
        {
            var count = Interlocked.Increment(ref _memoryCacheHits);
            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.Cache",
                "cache.memory.hit",
                "Зарегистрировано попадание в оперативный кэш.",
                CreateCountFields(count));
        }

        public void RecordPersistentCacheHit()
        {
            var count = Interlocked.Increment(ref _persistentCacheHits);
            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.Cache",
                "cache.persistent.hit",
                "Зарегистрировано попадание в дисковый кэш.",
                CreateCountFields(count));
        }

        public void RecordStaleCacheHit()
        {
            var count = Interlocked.Increment(ref _staleCacheHits);
            RecordDiagnosticEvent(
                LogLevel.Warning,
                "KinopoiskUnofficialInfo.ApiClient.Cache",
                "cache.stale.hit",
                "Использован устаревший ответ из кэша.",
                CreateCountFields(count));
        }

        public void RecordPersistentCacheWrite()
        {
            var count = Interlocked.Increment(ref _persistentCacheWrites);
            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.Cache",
                "cache.persistent.write",
                "Ответ записан в дисковый кэш.",
                CreateCountFields(count));
        }

        public void RecordImageCacheHit()
        {
            var count = Interlocked.Increment(ref _imageCacheHits);
            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.ImageCache",
                "image-cache.hit",
                "Зарегистрировано попадание в кэш изображений.",
                CreateCountFields(count));
        }

        public void RecordImageCacheMiss()
        {
            var count = Interlocked.Increment(ref _imageCacheMisses);
            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.ImageCache",
                "image-cache.miss",
                "Зарегистрирован промах кэша изображений.",
                CreateCountFields(count));
        }

        public void RecordImageCacheStaleHit()
        {
            var count = Interlocked.Increment(ref _imageCacheStaleHits);
            RecordDiagnosticEvent(
                LogLevel.Warning,
                "KinopoiskUnofficialInfo.ApiClient.ImageCache",
                "image-cache.stale.hit",
                "Использовано устаревшее изображение из кэша.",
                CreateCountFields(count));
        }

        public void RecordImageCacheWrite(long bytes)
        {
            var count = Interlocked.Increment(ref _imageCacheWrites);
            if (bytes > 0)
                Interlocked.Add(ref _imageCacheBytesWritten, bytes);

            RecordDiagnosticEvent(
                LogLevel.Trace,
                "KinopoiskUnofficialInfo.ApiClient.ImageCache",
                "image-cache.write",
                "Файл изображения записан в кэш.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["bytes"] = Math.Max(0, bytes).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        public void ResetRuntimeCounters()
        {
            Interlocked.Exchange(ref _apiRequests, 0);
            Interlocked.Exchange(ref _apiFailures, 0);
            Interlocked.Exchange(ref _memoryCacheHits, 0);
            Interlocked.Exchange(ref _persistentCacheHits, 0);
            Interlocked.Exchange(ref _staleCacheHits, 0);
            Interlocked.Exchange(ref _persistentCacheWrites, 0);
            Interlocked.Exchange(ref _imageCacheHits, 0);
            Interlocked.Exchange(ref _imageCacheMisses, 0);
            Interlocked.Exchange(ref _imageCacheStaleHits, 0);
            Interlocked.Exchange(ref _imageCacheWrites, 0);
            Interlocked.Exchange(ref _imageCacheBytesWritten, 0);

            lock (_quotaSync)
                _lastQuotaFailureUtc = null;
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

            RecordDiagnosticEvent(
                LogLevel.Information,
                "KinopoiskUnofficialInfo.ApiClient.Quota",
                "quota.updated",
                "Состояние квоты КиноПоиска обновлено.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["accountType"] = quota.AccountType ?? string.Empty,
                    ["dailyUsed"] = (quota.DailyQuota?.Used ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["dailyLimit"] = (quota.DailyQuota?.Value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["totalUsed"] = (quota.TotalQuota?.Used ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["totalLimit"] = (quota.TotalQuota?.Value ?? 0).ToString(System.Globalization.CultureInfo.InvariantCulture)
                });
        }

        public void RecordQuotaFailure()
        {
            lock (_quotaSync)
                _lastQuotaFailureUtc = DateTimeOffset.UtcNow;

            RecordDiagnosticEvent(
                LogLevel.Warning,
                "KinopoiskUnofficialInfo.ApiClient.Quota",
                "quota.failure",
                "Проверка квоты КиноПоиска завершилась ошибкой.");
        }

        public KinopoiskDiagnosticsSnapshot GetSnapshot()
        {
            var sinkSnapshot = GetDiagnosticSinkSnapshot();

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
                    TotalQuotaUsed = _totalQuotaUsed,
                    DiagnosticSinkAttached = sinkSnapshot.Attached,
                    DiagnosticEventsReceived = sinkSnapshot.EventsReceived,
                    DiagnosticEventsWritten = sinkSnapshot.EventsWritten,
                    DiagnosticWriteFailures = sinkSnapshot.WriteFailures,
                    DiagnosticLastWriteUtc = sinkSnapshot.LastWriteUtc,
                    DiagnosticCurrentFilePath = sinkSnapshot.CurrentFilePath,
                    DiagnosticLastEventName = sinkSnapshot.LastEventName,
                    DiagnosticLastError = sinkSnapshot.LastError
                };
            }
        }

        private IKinopoiskDiagnosticSink GetDiagnosticSink()
        {
            lock (_diagnosticSinkSync)
                return _diagnosticSink;
        }

        private KinopoiskDiagnosticSinkSnapshot GetDiagnosticSinkSnapshot()
        {
            var sink = GetDiagnosticSink();
            if (sink is null)
                return new KinopoiskDiagnosticSinkSnapshot();

            try
            {
                var snapshot = sink.GetSnapshot() ?? new KinopoiskDiagnosticSinkSnapshot();
                snapshot.Attached = true;
                return snapshot;
            }
            catch
            {
                return new KinopoiskDiagnosticSinkSnapshot
                {
                    Attached = true,
                    LastError = "Не удалось получить состояние диагностического канала."
                };
            }
        }

        private static IReadOnlyDictionary<string, string> CreateCountFields(long count)
        {
            return new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["count"] = count.ToString(System.Globalization.CultureInfo.InvariantCulture)
            };
        }
    }

    public interface IKinopoiskDiagnosticSink
    {
        void BeginSession();

        void Write(KinopoiskDiagnosticEvent diagnosticEvent);

        KinopoiskDiagnosticSinkSnapshot GetSnapshot();
    }

    public sealed class KinopoiskDiagnosticEvent
    {
        public DateTimeOffset TimestampUtc { get; set; }

        public LogLevel Level { get; set; }

        public string Category { get; set; } = string.Empty;

        public string EventName { get; set; } = string.Empty;

        public string Message { get; set; } = string.Empty;

        public IReadOnlyDictionary<string, string> Fields { get; set; }
    }

    public sealed class KinopoiskDiagnosticSinkSnapshot
    {
        public bool Attached { get; set; }

        public long EventsReceived { get; set; }

        public long EventsWritten { get; set; }

        public long WriteFailures { get; set; }

        public DateTimeOffset? LastWriteUtc { get; set; }

        public string CurrentFilePath { get; set; } = string.Empty;

        public string LastEventName { get; set; } = string.Empty;

        public string LastError { get; set; } = string.Empty;
    }

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

        public bool DiagnosticSinkAttached { get; set; }

        public long DiagnosticEventsReceived { get; set; }

        public long DiagnosticEventsWritten { get; set; }

        public long DiagnosticWriteFailures { get; set; }

        public DateTimeOffset? DiagnosticLastWriteUtc { get; set; }

        public string DiagnosticCurrentFilePath { get; set; } = string.Empty;

        public string DiagnosticLastEventName { get; set; } = string.Empty;

        public string DiagnosticLastError { get; set; } = string.Empty;
    }
}
