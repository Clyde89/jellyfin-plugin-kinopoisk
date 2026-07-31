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
    /// Формирует read-only отчёт локальных франшиз КиноПоиска.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewTask : IScheduledTask
    {
        private readonly KinopoiskFranchiseLibraryScanner _libraryScanner;
        private readonly KinopoiskFranchisePreviewService _previewService;
        private readonly KinopoiskFranchiseReportWriter _reportWriter;
        private readonly ILogger<KinopoiskFranchisePreviewTask> _logger;

        /// <summary>
        /// Инициализирует ручную задачу предварительного просмотра франшиз.
        /// </summary>
        public KinopoiskFranchisePreviewTask(
            KinopoiskFranchiseLibraryScanner libraryScanner,
            KinopoiskFranchisePreviewService previewService,
            KinopoiskFranchiseReportWriter reportWriter,
            ILogger<KinopoiskFranchisePreviewTask> logger)
        {
            _libraryScanner = libraryScanner
                ?? throw new ArgumentNullException(nameof(libraryScanner));
            _previewService = previewService
                ?? throw new ArgumentNullException(nameof(previewService));
            _reportWriter = reportWriter
                ?? throw new ArgumentNullException(nameof(reportWriter));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public string Name => "Предварительный просмотр франшиз КиноПоиска";

        /// <inheritdoc />
        public string Key => "KinopoiskFranchisePreview";

        /// <inheritdoc />
        public string Description
            => "Анализирует локальные фильмы и сериалы с Kinopoisk ID, загружает кэшируемые связи и сохраняет read-only JSON-отчёт. Коллекции Jellyfin не создаются и не изменяются.";

        /// <inheritdoc />
        public string Category => Constants.PluginName;

        /// <inheritdoc />
        public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
            => Enumerable.Empty<TaskTriggerInfo>();

        /// <inheritdoc />
        public async Task ExecuteAsync(
            IProgress<double> progress,
            CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(progress);
            cancellationToken.ThrowIfCancellationRequested();

            var configuration = Plugin.Instance.Configuration;
            configuration.Normalize();
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
            var previewProgress = new RangeProgress(progress, 5, 92);
            var previewResult = await _previewService
                .BuildPreview(
                    localItems,
                    plannerOptions,
                    previewProgress,
                    cancellationToken)
                .ConfigureAwait(false);
            progress.Report(95);

            var reportPath = await _reportWriter
                .WritePreview(
                    previewResult,
                    new KinopoiskFranchiseReportOptions
                    {
                        IncludeSeries = configuration.IncludeSeriesInFranchises,
                        IncludeSequels = configuration.IncludeFranchiseSequels,
                        IncludePrequels = configuration.IncludeFranchisePrequels,
                        IncludeRemakes = configuration.IncludeFranchiseRemakes,
                        MinimumItems = configuration.MinimumFranchiseItems,
                        CollectionNameSuffix = configuration.FranchiseCollectionNameSuffix,
                        CollectionWritesEnabled = configuration.EnableFranchiseCollections,
                        PreviewOnly = true
                    },
                    cancellationToken)
                .ConfigureAwait(false);

            progress.Report(100);
            _logger.LogInformation(
                "Предварительный просмотр франшиз завершён: локальных объектов {LocalItemCount}, планов {PlanCount}, ошибок связей {FailedRequestCount}, отчёт {ReportPath}",
                previewResult.LocalItemCount,
                previewResult.Plans.Count,
                previewResult.FailedRequestCount,
                reportPath);

            if (configuration.EnableFranchiseCollections
                && !configuration.FranchisePreviewOnly)
            {
                _logger.LogWarning(
                    "Запись коллекций запрошена конфигурацией, но текущая задача принудительно выполнена в read-only режиме");
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
