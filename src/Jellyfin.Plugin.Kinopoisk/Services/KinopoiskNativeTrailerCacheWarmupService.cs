#nullable enable

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Координирует ленивую подготовку MP4-трейлеров и исключает параллельную
    /// загрузку одного и того же ролика несколькими клиентами.
    /// </summary>
    public interface IKinopoiskNativeTrailerCacheWarmupService
    {
        void Start(int kinopoiskId);

        Task<KinopoiskNativeTrailerCacheEntry?> WaitForReady(
            int kinopoiskId,
            TimeSpan maximumWait,
            CancellationToken cancellationToken);
    }

    /// <inheritdoc />
    public sealed class KinopoiskNativeTrailerCacheWarmupService
        : IKinopoiskNativeTrailerCacheWarmupService
    {
        private readonly IKinopoiskTrailerPlaybackService _playbackService;
        private readonly IKinopoiskNativeTrailerCache _cache;
        private readonly IHostApplicationLifetime _applicationLifetime;
        private readonly ILogger<KinopoiskNativeTrailerCacheWarmupService> _logger;
        private readonly ConcurrentDictionary<
            int,
            Lazy<Task<KinopoiskNativeTrailerCacheEntry?>>> _operations = new();

        public KinopoiskNativeTrailerCacheWarmupService(
            IKinopoiskTrailerPlaybackService playbackService,
            IKinopoiskNativeTrailerCache cache,
            IHostApplicationLifetime applicationLifetime,
            ILogger<KinopoiskNativeTrailerCacheWarmupService> logger)
        {
            _playbackService = playbackService
                ?? throw new ArgumentNullException(nameof(playbackService));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _applicationLifetime = applicationLifetime
                ?? throw new ArgumentNullException(nameof(applicationLifetime));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public void Start(int kinopoiskId)
        {
            if (!_cache.Enabled || kinopoiskId < 1 || _cache.TryGet(kinopoiskId, false) is not null)
                return;

            _ = Warmup(kinopoiskId);
        }

        /// <inheritdoc />
        public async Task<KinopoiskNativeTrailerCacheEntry?> WaitForReady(
            int kinopoiskId,
            TimeSpan maximumWait,
            CancellationToken cancellationToken)
        {
            var cached = _cache.TryGet(kinopoiskId);
            if (cached is not null || !_cache.Enabled || maximumWait <= TimeSpan.Zero)
                return cached;

            var warmup = Warmup(kinopoiskId);
            try
            {
                return await warmup
                    .WaitAsync(maximumWait, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogInformation(
                    "Локальный трейлер для Kinopoisk ID {KinopoiskId} не подготовлен за {WaitSeconds} секунд; используется потоковый резерв",
                    kinopoiskId,
                    maximumWait.TotalSeconds);
                return _cache.TryGet(kinopoiskId);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return _cache.TryGet(kinopoiskId);
            }
        }

        private async Task<KinopoiskNativeTrailerCacheEntry?> Warmup(int kinopoiskId)
        {
            var cached = _cache.TryGet(kinopoiskId);
            if (cached is not null)
                return cached;

            var operation = _operations.GetOrAdd(
                kinopoiskId,
                id => new Lazy<Task<KinopoiskNativeTrailerCacheEntry?>>(
                    () => WarmupCore(id),
                    LazyThreadSafetyMode.ExecutionAndPublication));
            try
            {
                return await operation.Value.ConfigureAwait(false);
            }
            finally
            {
                if (_operations.TryGetValue(kinopoiskId, out var current)
                    && ReferenceEquals(current, operation))
                {
                    _operations.TryRemove(kinopoiskId, out _);
                }
            }
        }

        private async Task<KinopoiskNativeTrailerCacheEntry?> WarmupCore(int kinopoiskId)
        {
            try
            {
                var playback = await _playbackService
                    .Get(kinopoiskId, _applicationLifetime.ApplicationStopping)
                    .ConfigureAwait(false);
                if (!IsKinopoiskHls(playback))
                {
                    _logger.LogInformation(
                        "Локальный MP4 не подготовлен для Kinopoisk ID {KinopoiskId}: HLS КиноПоиска отсутствует",
                        kinopoiskId);
                    return null;
                }

                var entry = await _cache
                    .GetOrCreate(
                        kinopoiskId,
                        playback!.Selected,
                        _applicationLifetime.ApplicationStopping)
                    .ConfigureAwait(false);
                if (entry is not null)
                {
                    _logger.LogInformation(
                        "Локальный MP4-трейлер подготовлен для Kinopoisk ID {KinopoiskId}: {Path}",
                        kinopoiskId,
                        entry.Path);
                }

                return entry;
            }
            catch (OperationCanceledException) when (_applicationLifetime.ApplicationStopping.IsCancellationRequested)
            {
                return null;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Фоновая подготовка локального трейлера для Kinopoisk ID {KinopoiskId} завершилась ошибкой",
                    kinopoiskId);
                return null;
            }
        }

        private static bool IsKinopoiskHls(KinopoiskTrailerPlaybackResponse? playback)
            => playback is not null
                && string.Equals(
                    playback.Selected.Provider,
                    "kinopoisk",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    playback.Selected.PlaybackKind,
                    "hls",
                    StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(
                    playback.Selected.Url,
                    UriKind.Absolute,
                    out var mediaUri)
                && mediaUri.Scheme == Uri.UriSchemeHttps;
    }
}
