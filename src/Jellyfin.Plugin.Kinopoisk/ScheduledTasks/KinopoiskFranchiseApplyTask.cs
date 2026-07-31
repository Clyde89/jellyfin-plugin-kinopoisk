using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.ScheduledTasks
{
    /// <summary>
    /// Безопасно применяет предварительно просмотренный план управляемых коллекций.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyTask : IScheduledTask
    {
        private static readonly TimeSpan MaximumPreviewAge = TimeSpan.FromHours(24);

        private readonly KinopoiskFranchiseLibraryScanner _libraryScanner;
        private readonly KinopoiskFranchisePreviewService _previewService;
        private readonly KinopoiskFranchisePreviewReportReader _previewReportReader;
        private readonly KinopoiskFranchiseApplyService _applyService;
        private readonly KinopoiskFranchiseApplyReportWriter _applyReportWriter;
        private readonly ILogger<KinopoiskFranchiseApplyTask> _logger;

        public KinopoiskFranchiseApplyTask(
            KinopoiskFranchiseLibraryScanner libraryScanner,
            KinopoiskFranchisePreviewService previewService,
            KinopoiskFranchisePreviewReportReader previewReportReader,
            KinopoiskFranchiseApplyService applyService,
            KinopoiskFranchiseApplyReportWriter applyReportWriter,
            ILogger<KinopoiskFranchiseApplyTask> logger)
        {
            _libraryScanner = libraryScanner ?? throw new ArgumentNullException(nameof(libraryScanner));
            _previewService = previewService ?? throw new ArgumentNullException(nameof(previewService));
            _previewReportReader = previewReportReader ?? throw new ArgumentNullException(nameof(previewReportReader));
            _applyService = applyService ?? throw new ArgumentNullException(nameof(applyService));
            _applyReportWriter = applyReportWriter ?? throw new ArgumentNullException(nameof(applyReportWriter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public string Name => "Применение управляемых коллекций КиноПоиска";

        public string Key => "KinopoiskFranchiseApply";

        public string Description
            => "Создаёт только помеченные плагином BoxSet и добавляет недостающие локальные элементы. Требует свежего совпадающего preview-отчёта; ручные коллекции, названия и существующие элементы не изменяются.";

        public string Category => Constants.PluginName;

        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => Enumerable.Empty<TaskTriggerInfo>();

        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();

            var configuration = Plugin.Instance.Configuration;
            configuration.Normalize();
            if (!configuration.EnableFranchiseCollections
                || configuration.FranchisePreviewOnly)
            {
                throw new InvalidOperationException(
                    "Применение коллекций заблокировано настройками. Сначала выполните предварительный просмотр, затем явно разрешите управляемые коллекции и отключите режим только предварительного просмотра.");
            }

            if (!configuration.PreserveManualCollections)
                throw new InvalidOperationException("Защита ручных коллекций должна оставаться включённой.");
            if (configuration.RemoveMissingItemsFromManagedCollections)
            {
                throw new InvalidOperationException(
                    "Удаление элементов из управляемых коллекций не поддерживается безопасным этапом применения.");
            }

            progress.Report(2);
            var localItems = _libraryScanner.Scan(configuration.IncludeSeriesInFranchises);
            progress.Report(5);
            var plannerOptions = new KinopoiskFranchisePlannerOptions
            {
                IncludeSequels = configuration.IncludeFranchiseSequels,
                IncludePrequels = configuration.IncludeFranchisePrequels,
                IncludeRemakes = configuration.IncludeFranchiseRemakes,
                MinimumItems = configuration.MinimumFranchiseItems,
                CollectionNameSuffix = configuration.FranchiseCollectionNameSuffix
            };
            var previewResult = await _previewService
                .BuildPreview(
                    localItems,
                    plannerOptions,
                    new RangeProgress(progress, 5, 55),
                    cancellationToken)
                .ConfigureAwait(false);
            if (previewResult.FailedRequestCount > 0)
            {
                throw new InvalidOperationException(
                    "Текущий план содержит ошибки загрузки связей. Коллекции не применены; повторите предварительный просмотр после восстановления API.");
            }

            var reportOptions = new KinopoiskFranchiseReportOptions
            {
                IncludeSeries = configuration.IncludeSeriesInFranchises,
                IncludeSequels = configuration.IncludeFranchiseSequels,
                IncludePrequels = configuration.IncludeFranchisePrequels,
                IncludeRemakes = configuration.IncludeFranchiseRemakes,
                MinimumItems = configuration.MinimumFranchiseItems,
                CollectionNameSuffix = configuration.FranchiseCollectionNameSuffix,
                CollectionWritesEnabled = true,
                PreviewOnly = false
            };
            var fingerprint = KinopoiskFranchisePlanFingerprint.Compute(
                previewResult.Plans,
                reportOptions);
            await _previewReportReader
                .ReadValidated(fingerprint, MaximumPreviewAge, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(60);

            var applyResult = await _applyService
                .Apply(
                    previewResult.Plans,
                    new RangeProgress(progress, 60, 95),
                    cancellationToken)
                .ConfigureAwait(false);
            var reportPath = await _applyReportWriter
                .Write(fingerprint, applyResult, cancellationToken)
                .ConfigureAwait(false);
            progress.Report(100);

            _logger.LogInformation(
                "Управляемые коллекции применены: создано {CreatedCount}, дополнено {UpdatedCount}, без изменений {UnchangedCount}, конфликтов {ConflictCount}, ошибок {FailureCount}, добавлено элементов {AddedItemCount}, отчёт {ReportPath}",
                applyResult.CreatedCollectionCount,
                applyResult.UpdatedCollectionCount,
                applyResult.UnchangedCollectionCount,
                applyResult.ConflictCount,
                applyResult.FailureCount,
                applyResult.AddedItemCount,
                reportPath);

            if (applyResult.ConflictCount > 0 || applyResult.FailureCount > 0)
            {
                throw new InvalidOperationException(
                    "Применение завершено с конфликтами или ошибками. Проверьте отчёт franchise-apply-latest.json.");
            }
        }

        private sealed class RangeProgress : IProgress<double>
        {
            private readonly IProgress<double> _inner;
            private readonly double _start;
            private readonly double _range;

            public RangeProgress(IProgress<double> inner, double start, double end)
            {
                _inner = inner;
                _start = start;
                _range = Math.Max(0, end - start);
            }

            public void Report(double value)
            {
                var normalized = Math.Clamp(value, 0, 100) / 100d;
                _inner.Report(_start + (_range * normalized));
            }
        }
    }
}
