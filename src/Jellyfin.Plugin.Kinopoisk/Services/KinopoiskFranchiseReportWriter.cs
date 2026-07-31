using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит безопасный снимок параметров предварительного просмотра франшиз.
    /// </summary>
    public sealed class KinopoiskFranchiseReportOptions
    {
        /// <summary>
        /// Получает или задаёт признак включения сериалов.
        /// </summary>
        public bool IncludeSeries { get; set; }

        /// <summary>
        /// Получает или задаёт признак учёта сиквелов.
        /// </summary>
        public bool IncludeSequels { get; set; }

        /// <summary>
        /// Получает или задаёт признак учёта приквелов.
        /// </summary>
        public bool IncludePrequels { get; set; }

        /// <summary>
        /// Получает или задаёт признак учёта ремейков.
        /// </summary>
        public bool IncludeRemakes { get; set; }

        /// <summary>
        /// Получает или задаёт минимальное количество локальных объектов.
        /// </summary>
        public int MinimumItems { get; set; }

        /// <summary>
        /// Получает или задаёт суффикс названия коллекции.
        /// </summary>
        public string CollectionNameSuffix { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт признак разрешения записи управляемых коллекций.
        /// </summary>
        public bool CollectionWritesEnabled { get; set; }

        /// <summary>
        /// Получает или задаёт признак режима предварительного просмотра.
        /// </summary>
        public bool PreviewOnly { get; set; } = true;
    }

    /// <summary>
    /// Содержит сохранённый read-only отчёт планирования франшиз.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewReport
    {
        /// <summary>
        /// Получает или задаёт версию схемы отчёта.
        /// </summary>
        public int SchemaVersion { get; set; } = 1;

        /// <summary>
        /// Получает или задаёт время формирования отчёта в UTC.
        /// </summary>
        public DateTimeOffset GeneratedAtUtc { get; set; }

        /// <summary>
        /// Получает или задаёт режим выполнения.
        /// </summary>
        public string Mode { get; set; } = "preview";

        /// <summary>
        /// Получает или задаёт безопасный снимок параметров.
        /// </summary>
        public KinopoiskFranchiseReportOptions Options { get; set; } = new();

        /// <summary>
        /// Получает или задаёт сводку выполнения.
        /// </summary>
        public KinopoiskFranchiseReportSummary Summary { get; set; } = new();

        /// <summary>
        /// Получает или задаёт планы локальных коллекций.
        /// </summary>
        public IReadOnlyList<KinopoiskFranchisePlan> Plans { get; set; }
            = Array.Empty<KinopoiskFranchisePlan>();
    }

    /// <summary>
    /// Содержит сводные показатели предварительного просмотра.
    /// </summary>
    public sealed class KinopoiskFranchiseReportSummary
    {
        /// <summary>
        /// Получает или задаёт количество локальных объектов.
        /// </summary>
        public int LocalItemCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество обработанных объектов.
        /// </summary>
        public int ProcessedItemCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество неудачных запросов.
        /// </summary>
        public int FailedRequestCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество запланированных коллекций.
        /// </summary>
        public int PlannedCollectionCount { get; set; }

        /// <summary>
        /// Получает или задаёт количество уникальных объектов в планах.
        /// </summary>
        public int PlannedItemCount { get; set; }
    }

    /// <summary>
    /// Атомарно сохраняет read-only отчёты франшиз в каталоге данных плагина.
    /// </summary>
    public sealed class KinopoiskFranchiseReportWriter
    {
        private const int MaximumArchivedReports = 10;
        private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();

        private readonly string _reportDirectory;
        private readonly ILogger<KinopoiskFranchiseReportWriter> _logger;

        /// <summary>
        /// Инициализирует средство записи отчётов.
        /// </summary>
        /// <param name="reportDirectory">Каталог отчётов.</param>
        /// <param name="logger">Журнал.</param>
        public KinopoiskFranchiseReportWriter(
            string reportDirectory,
            ILogger<KinopoiskFranchiseReportWriter> logger)
        {
            if (string.IsNullOrWhiteSpace(reportDirectory))
                throw new ArgumentException("Каталог отчётов не должен быть пустым.", nameof(reportDirectory));

            _reportDirectory = Path.GetFullPath(reportDirectory);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Формирует и атомарно сохраняет отчёт предварительного просмотра.
        /// </summary>
        /// <param name="previewResult">Результат предварительного просмотра.</param>
        /// <param name="options">Безопасный снимок параметров.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Полный путь к актуальному отчёту.</returns>
        public async Task<string> WritePreview(
            KinopoiskFranchisePreviewResult previewResult,
            KinopoiskFranchiseReportOptions options,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(previewResult);
            ArgumentNullException.ThrowIfNull(options);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_reportDirectory);
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var report = new KinopoiskFranchisePreviewReport
            {
                GeneratedAtUtc = generatedAtUtc,
                Options = options,
                Summary = new KinopoiskFranchiseReportSummary
                {
                    LocalItemCount = previewResult.LocalItemCount,
                    ProcessedItemCount = previewResult.ProcessedItemCount,
                    FailedRequestCount = previewResult.FailedRequestCount,
                    PlannedCollectionCount = previewResult.Plans.Count,
                    PlannedItemCount = previewResult.Plans
                        .SelectMany(plan => plan.Items)
                        .Select(item => item.ItemId)
                        .Distinct()
                        .Count()
                },
                Plans = previewResult.Plans
            };

            var latestPath = Path.Combine(_reportDirectory, "franchise-preview-latest.json");
            var archivePath = Path.Combine(
                _reportDirectory,
                $"franchise-preview-{generatedAtUtc:yyyyMMdd-HHmmssfffffff}.json");
            var temporaryPath = latestPath + ".tmp-" + Guid.NewGuid().ToString("N");

            try
            {
                await using (var stream = new FileStream(
                    temporaryPath,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None,
                    65536,
                    FileOptions.Asynchronous | FileOptions.WriteThrough))
                {
                    await JsonSerializer.SerializeAsync(
                            stream,
                            report,
                            SerializerOptions,
                            cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, latestPath, overwrite: true);
                File.Copy(latestPath, archivePath, overwrite: false);
                RotateArchivedReports();

                _logger.LogInformation(
                    "Отчёт предварительного просмотра франшиз сохранён: {ReportPath}",
                    latestPath);
                return latestPath;
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private void RotateArchivedReports()
        {
            var archivedReports = Directory
                .EnumerateFiles(_reportDirectory, "franchise-preview-*.json")
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "franchise-preview-latest.json",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaximumArchivedReports)
                .ToArray();

            foreach (var report in archivedReports)
            {
                try
                {
                    report.Delete();
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Устаревший отчёт франшиз не удалён: {ReportPath}",
                        report.FullName);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning(
                        exception,
                        "Недостаточно прав для удаления устаревшего отчёта франшиз: {ReportPath}",
                        report.FullName);
                }
            }
        }

        private static JsonSerializerOptions CreateSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
