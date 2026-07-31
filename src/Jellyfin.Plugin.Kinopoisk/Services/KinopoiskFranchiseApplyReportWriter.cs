using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Содержит отчёт безопасного применения управляемых коллекций.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyReport
    {
        public int SchemaVersion { get; set; } = 1;
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public string Mode { get; set; } = "apply";
        public string PlanFingerprint { get; set; } = string.Empty;
        public KinopoiskFranchiseApplyResult Result { get; set; } = new();
    }

    /// <summary>
    /// Атомарно сохраняет отчёт применения управляемых коллекций.
    /// </summary>
    public sealed class KinopoiskFranchiseApplyReportWriter
    {
        private const int MaximumArchivedReports = 10;
        private readonly string _reportDirectory;
        private readonly ILogger<KinopoiskFranchiseApplyReportWriter> _logger;

        public KinopoiskFranchiseApplyReportWriter(
            string reportDirectory,
            ILogger<KinopoiskFranchiseApplyReportWriter> logger)
        {
            if (string.IsNullOrWhiteSpace(reportDirectory))
                throw new ArgumentException("Каталог отчётов не должен быть пустым.", nameof(reportDirectory));

            _reportDirectory = Path.GetFullPath(reportDirectory);
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<string> Write(
            string planFingerprint,
            KinopoiskFranchiseApplyResult result,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(planFingerprint))
                throw new ArgumentException("Отпечаток плана не должен быть пустым.", nameof(planFingerprint));
            ArgumentNullException.ThrowIfNull(result);
            cancellationToken.ThrowIfCancellationRequested();

            Directory.CreateDirectory(_reportDirectory);
            var generatedAtUtc = DateTimeOffset.UtcNow;
            var report = new KinopoiskFranchiseApplyReport
            {
                GeneratedAtUtc = generatedAtUtc,
                PlanFingerprint = planFingerprint,
                Result = result
            };
            var latestPath = Path.Combine(_reportDirectory, "franchise-apply-latest.json");
            var archivePath = Path.Combine(
                _reportDirectory,
                $"franchise-apply-{generatedAtUtc:yyyyMMdd-HHmmssfffffff}.json");
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
                            KinopoiskFranchiseReportWriter.CreateSerializerOptions(),
                            cancellationToken)
                        .ConfigureAwait(false);
                    await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
                }

                File.Move(temporaryPath, latestPath, overwrite: true);
                File.Copy(latestPath, archivePath, overwrite: false);
                RotateArchivedReports();
                _logger.LogInformation(
                    "Отчёт применения управляемых коллекций сохранён: {ReportPath}",
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
            var files = Directory
                .EnumerateFiles(_reportDirectory, "franchise-apply-*.json")
                .Where(path => !string.Equals(
                    Path.GetFileName(path),
                    "franchise-apply-latest.json",
                    StringComparison.OrdinalIgnoreCase))
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.CreationTimeUtc)
                .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
                .Skip(MaximumArchivedReports)
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch (IOException exception)
                {
                    _logger.LogWarning(exception, "Устаревший отчёт применения не удалён: {ReportPath}", file.FullName);
                }
                catch (UnauthorizedAccessException exception)
                {
                    _logger.LogWarning(exception, "Недостаточно прав для удаления отчёта применения: {ReportPath}", file.FullName);
                }
            }
        }
    }
}
