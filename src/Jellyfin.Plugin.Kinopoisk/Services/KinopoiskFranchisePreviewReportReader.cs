using System;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Загружает и проверяет последний отчёт предварительного просмотра франшиз.
    /// </summary>
    public sealed class KinopoiskFranchisePreviewReportReader
    {
        private readonly string _reportPath;

        /// <summary>
        /// Инициализирует средство чтения актуального отчёта.
        /// </summary>
        /// <param name="reportDirectory">Каталог отчётов плагина.</param>
        public KinopoiskFranchisePreviewReportReader(string reportDirectory)
        {
            if (string.IsNullOrWhiteSpace(reportDirectory))
                throw new ArgumentException("Каталог отчётов не должен быть пустым.", nameof(reportDirectory));

            _reportPath = Path.Combine(
                Path.GetFullPath(reportDirectory),
                "franchise-preview-latest.json");
        }

        /// <summary>
        /// Загружает актуальный отчёт и проверяет схему, режим, срок и отпечаток плана.
        /// </summary>
        /// <param name="expectedFingerprint">Ожидаемый отпечаток текущего плана.</param>
        /// <param name="maximumAge">Максимальный допустимый возраст отчёта.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Проверенный отчёт.</returns>
        public async Task<KinopoiskFranchisePreviewReport> ReadValidated(
            string expectedFingerprint,
            TimeSpan maximumAge,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(expectedFingerprint))
                throw new ArgumentException("Ожидаемый отпечаток не должен быть пустым.", nameof(expectedFingerprint));
            if (maximumAge <= TimeSpan.Zero)
                throw new ArgumentOutOfRangeException(nameof(maximumAge));
            if (!File.Exists(_reportPath))
            {
                throw new InvalidOperationException(
                    "Актуальный отчёт предварительного просмотра не найден. Сначала запустите задачу предварительного просмотра франшиз КиноПоиска.");
            }

            await using var stream = new FileStream(
                _reportPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                65536,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var report = await JsonSerializer.DeserializeAsync<KinopoiskFranchisePreviewReport>(
                    stream,
                    KinopoiskFranchiseReportWriter.CreateSerializerOptions(),
                    cancellationToken)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException("Отчёт предварительного просмотра пуст или повреждён.");

            if (report.SchemaVersion != 1
                || !string.Equals(report.Mode, "preview", StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Версия или режим отчёта предварительного просмотра не поддерживается.");
            }

            var age = DateTimeOffset.UtcNow - report.GeneratedAtUtc;
            if (age < TimeSpan.Zero || age > maximumAge)
            {
                throw new InvalidOperationException(
                    "Отчёт предварительного просмотра устарел. Повторно запустите предварительный просмотр перед применением коллекций.");
            }

            if (!string.Equals(
                report.PlanFingerprint,
                expectedFingerprint,
                StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Текущий план франшиз отличается от предварительно просмотренного отчёта. Повторно запустите предварительный просмотр.");
            }

            return report;
        }
    }
}
