using System;
using System.IO;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchisePreviewReportReaderTests
    {
        [Fact]
        public async Task ShouldAcceptFreshReportWithMatchingFingerprint()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var fingerprint = await WritePreview(directory);
                var reader = new KinopoiskFranchisePreviewReportReader(directory);

                var report = await reader.ReadValidated(
                    fingerprint,
                    TimeSpan.FromHours(24));

                Assert.Equal(fingerprint, report.PlanFingerprint);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldRejectMissingReport()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var reader = new KinopoiskFranchisePreviewReportReader(directory);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    reader.ReadValidated(new string('a', 64), TimeSpan.FromHours(24)));

                Assert.Contains("не найден", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldRejectMismatchedFingerprint()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                await WritePreview(directory);
                var reader = new KinopoiskFranchisePreviewReportReader(directory);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    reader.ReadValidated(new string('b', 64), TimeSpan.FromHours(24)));

                Assert.Contains("отличается", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldRejectExpiredReport()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var fingerprint = await WritePreview(directory);
                var path = Path.Combine(directory, "franchise-preview-latest.json");
                var json = JsonNode.Parse(await File.ReadAllTextAsync(path)).AsObject();
                json["generatedAtUtc"] = DateTimeOffset.UtcNow
                    .Subtract(TimeSpan.FromDays(2))
                    .ToString("O");
                await File.WriteAllTextAsync(path, json.ToJsonString());
                var reader = new KinopoiskFranchisePreviewReportReader(directory);

                var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    reader.ReadValidated(fingerprint, TimeSpan.FromHours(24)));

                Assert.Contains("устарел", exception.Message, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(directory, recursive: true);
            }
        }

        private static async Task<string> WritePreview(string directory)
        {
            var result = new KinopoiskFranchisePreviewResult
            {
                LocalItemCount = 2,
                ProcessedItemCount = 2,
                Plans = new[]
                {
                    new KinopoiskFranchisePlan
                    {
                        AnchorKinopoiskId = 100,
                        SuggestedName = "Тестовая коллекция",
                        Items = new[]
                        {
                            Item(1, 100),
                            Item(2, 101)
                        }
                    }
                }
            };
            var options = new KinopoiskFranchiseReportOptions
            {
                IncludeSeries = true,
                IncludeSequels = true,
                IncludePrequels = true,
                MinimumItems = 2,
                CollectionNameSuffix = " — коллекция",
                PreviewOnly = true
            };
            var writer = new KinopoiskFranchiseReportWriter(
                directory,
                NullLogger<KinopoiskFranchiseReportWriter>.Instance);
            await writer.WritePreview(result, options);
            return KinopoiskFranchisePlanFingerprint.Compute(result.Plans, options);
        }

        private static KinopoiskFranchiseLibraryItem Item(int localId, int kinopoiskId)
            => new()
            {
                ItemId = Guid.Parse($"00000000-0000-0000-0000-{localId:D12}"),
                KinopoiskId = kinopoiskId,
                Name = $"Фильм {kinopoiskId}",
                ProductionYear = 2000 + localId
            };

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-franchise-reader-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
