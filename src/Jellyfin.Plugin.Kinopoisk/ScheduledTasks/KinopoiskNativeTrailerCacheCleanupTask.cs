using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.ScheduledTasks
{
    /// <summary>
    /// Еженедельно удаляет неиспользуемые и вытесненные локальные трейлеры.
    /// </summary>
    public sealed class KinopoiskNativeTrailerCacheCleanupTask : IScheduledTask
    {
        private readonly IKinopoiskNativeTrailerCache _cache;
        private readonly ILogger<KinopoiskNativeTrailerCacheCleanupTask> _logger;

        public KinopoiskNativeTrailerCacheCleanupTask(
            IKinopoiskNativeTrailerCache cache,
            ILogger<KinopoiskNativeTrailerCacheCleanupTask> logger)
        {
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Очистка локального кэша трейлеров КиноПоиска";

        public string Key => "KinopoiskNativeTrailerCacheCleanup";

        public string Description
            => "Удаляет локальные трейлеры, которыми не пользовались заданное время, и ограничивает общий размер кэша методом LRU.";

        public string Category => Constants.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => new[]
            {
                new TaskTriggerInfo
                {
                    Type = TaskTriggerInfoType.WeeklyTrigger,
                    DayOfWeek = DayOfWeek.Sunday,
                    TimeOfDayTicks = TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(15)).Ticks,
                    MaxRuntimeTicks = TimeSpan.FromMinutes(30).Ticks
                }
            };

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            progress.Report(5);
            var result = await _cache.Cleanup(cancellationToken).ConfigureAwait(false);
            progress.Report(100);
            _logger.LogInformation(
                "Очистка локального кэша трейлеров завершена: устаревших {Expired}, вытеснено по лимиту {Capacity}, временных {Temporary}, освобождено {RemovedBytes} байт, осталось {RemainingFiles} файлов ({RemainingBytes} байт)",
                result.RemovedExpiredFiles,
                result.RemovedCapacityFiles,
                result.RemovedTemporaryFiles,
                result.RemovedBytes,
                result.RemainingFiles,
                result.RemainingBytes);
        }
    }
}
