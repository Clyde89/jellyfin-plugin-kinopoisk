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
        public bool IncludeSeries { get; set; }
        public bool IncludeSequels { get; set; }
        public bool IncludePrequels { get; set; }
        public bool IncludeRemakes { get; set; }
        public int MinimumItems { get; set; }
        public string CollectionNameSuffix { get; set; } = string.Empty;
        public bool CollectionWritesEnabled { get; set; }
        public bool PreviewOnly { get; set; } = true;
    }

    /// <summary>
    /// Содержит сохранённый read-only отчёт планирования франшиз.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewReport
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public string Mode { get; set; } = "preview";
        public string PlanFingerprint { get; set; } = string.Empty;
        public KinopoiskFranchiseReportOptions Options { get; set; } = new();
        public KinopoiskFranchiseReportSummary Summary { get; set; } = new();
        public IReadOnlyList<KinopoiskFranchisePlan> Plans { get; set; }
            = Array.Empty<KinopoiskFranchisePlan>();
    }

    /// <summary>
    /// Содержит сводные показатели предварительного просмотра.
    /// </summary>
    public sealed class KinopoiskFranchiseReportSummary
    {
        public int LocalItemCount { get; set; }
        public int ProcessedItemCount { get; set; }
        public int FailedRequestCount { get; set; }
        public int PlannedCollectionCount { get; set; }
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

        public KinopoiskFranchiseReportWriter(
            string reportDirectory,
            ILogger<KinopoiskFranchiseReportWriter> logger)
        {
            if (string.IsNullOrWhiteSpace(reportDirectory))
                throw new ArgumentException("Каталог отчётов не должен быть пустым.", nameof(reportDirectory));

            _reportDirectory = Path.GetFullPath(reportDirectory);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

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
                PlanFingerprint = KinopoiskFranchisePlanFingerprint.Compute(
                    previewResult.Plans,
                    options),
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
                    _logger.LogWarning(exception, "Устаревший отчёт франшиз не удалён: {ReportPath}", report.FullName);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning(exception, "Недостаточно прав для удаления устаревшего отчёта франшиз: {ReportPath}", report.FullName);
                }
            }
        }

        internal static JsonSerializerOptions CreateSerializerOptions()
        {
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true
            };
            options.Converters.Add(new JsonStringEnumConverter());
            return options;
        }
    }
}
