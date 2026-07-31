using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseApplyReportWriterTests
    {
        [Fact]
        public async Task ShouldWriteLatestAndArchivedApplyReportsAtomically()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseApplyReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseApplyReportWriter>.Instance);

                var path = await writer.Write(Fingerprint, CreateResult());

                Assert.Equal(
                    Path.Combine(reportDirectory, "franchise-apply-latest.json"),
                    path);
                Assert.True(File.Exists(path));
                Assert.Single(Directory.EnumerateFiles(
                    reportDirectory,
                    "franchise-apply-????????-?????????????.json"));
                Assert.Empty(Directory.EnumerateFiles(reportDirectory, "*.tmp-*"));

                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("apply", root.GetProperty("mode").GetString());
                Assert.Equal(Fingerprint, root.GetProperty("planFingerprint").GetString());
                Assert.Equal(1, root.GetProperty("result").GetProperty("createdCollectionCount").GetInt32());
                Assert.Equal(2, root.GetProperty("result").GetProperty("addedItemCount").GetInt32());
            }
            finally
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldNotWriteSecretsOrAbsolutePathsIntoApplyReport()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseApplyReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseApplyReportWriter>.Instance);

                var path = await writer.Write(Fingerprint, CreateResult());
                var json = await File.ReadAllTextAsync(path);

                Assert.DoesNotContain("ApiToken", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain("test-token", json, StringComparison.OrdinalIgnoreCase);
                Assert.DoesNotContain(reportDirectory, json, StringComparison.OrdinalIgnoreCase);
            }
            finally
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldKeepNoMoreThanTenArchivedApplyReports()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseApplyReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseApplyReportWriter>.Instance);

                for (var index = 0; index < 12; index++)
                {
                    await writer.Write(Fingerprint, CreateResult());
                    await Task.Delay(2);
                }

                var archives = Directory
                    .EnumerateFiles(reportDirectory, "franchise-apply-*.json")
                    .Where(path => !path.EndsWith(
                        "franchise-apply-latest.json",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.Equal(10, archives.Length);
            }
            finally
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }

        private const string Fingerprint
            = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

        private static KinopoiskFranchiseApplyResult CreateResult()
            => new()
            {
                PlanCount = 1,
                CreatedCollectionCount = 1,
                AddedItemCount = 2,
                Items = new[]
                {
                    new KinopoiskFranchiseApplyItemResult
                    {
                        AnchorKinopoiskId = 100,
                        SuggestedName = "Тестовая франшиза — коллекция",
                        CollectionId = Guid.Parse("00000000-0000-0000-0000-000000000100"),
                        Action = "created",
                        AddedItemCount = 2
                    }
                }
            };

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-franchise-apply-report-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
