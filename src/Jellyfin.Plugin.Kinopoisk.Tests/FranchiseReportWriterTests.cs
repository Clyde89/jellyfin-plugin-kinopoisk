using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseReportWriterTests
    {
        [Fact]
        public async Task ShouldWriteLatestAndArchivedPreviewReportsAtomically()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseReportWriter>.Instance);

                var path = await writer.WritePreview(CreatePreviewResult(), CreateOptions());

                Assert.Equal(
                    Path.Combine(reportDirectory, "franchise-preview-latest.json"),
                    path);
                Assert.True(File.Exists(path));
                Assert.Single(Directory.EnumerateFiles(
                    reportDirectory,
                    "franchise-preview-????????-?????????????.json"));
                Assert.Empty(Directory.EnumerateFiles(reportDirectory, "*.tmp-*"));

                using var document = JsonDocument.Parse(await File.ReadAllTextAsync(path));
                var root = document.RootElement;
                Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
                Assert.Equal("preview", root.GetProperty("mode").GetString());
                Assert.True(root.GetProperty("options").GetProperty("previewOnly").GetBoolean());
                Assert.False(root.GetProperty("options").GetProperty("collectionWritesEnabled").GetBoolean());
                Assert.Equal(1, root.GetProperty("summary").GetProperty("plannedCollectionCount").GetInt32());
                Assert.Equal(2, root.GetProperty("summary").GetProperty("plannedItemCount").GetInt32());
                Assert.Equal(
                    "SEQUEL",
                    root.GetProperty("plans")[0].GetProperty("relationTypes")[0].GetString());
            }
            finally
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldNotWriteSecretsIntoPreviewReport()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseReportWriter>.Instance);
                var path = await writer.WritePreview(CreatePreviewResult(), CreateOptions());

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
        public async Task ShouldKeepNoMoreThanTenArchivedReports()
        {
            var reportDirectory = CreateTemporaryDirectory();
            try
            {
                var writer = new KinopoiskFranchiseReportWriter(
                    reportDirectory,
                    NullLogger<KinopoiskFranchiseReportWriter>.Instance);

                for (var index = 0; index < 12; index++)
                {
                    await writer.WritePreview(CreatePreviewResult(), CreateOptions());
                    await Task.Delay(2);
                }

                var archives = Directory
                    .EnumerateFiles(reportDirectory, "franchise-preview-*.json")
                    .Where(path => !path.EndsWith(
                        "franchise-preview-latest.json",
                        StringComparison.OrdinalIgnoreCase))
                    .ToArray();
                Assert.Equal(10, archives.Length);
            }
            finally
            {
                Directory.Delete(reportDirectory, recursive: true);
            }
        }

        private static KinopoiskFranchisePreviewResult CreatePreviewResult()
        {
            return new KinopoiskFranchisePreviewResult
            {
                LocalItemCount = 2,
                ProcessedItemCount = 2,
                FailedRequestCount = 0,
                Plans = new[]
                {
                    new KinopoiskFranchisePlan
                    {
                        AnchorKinopoiskId = 100,
                        SuggestedName = "Тестовая франшиза — коллекция",
                        Items = new[]
                        {
                            new KinopoiskFranchiseLibraryItem
                            {
                                ItemId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                                KinopoiskId = 100,
                                Name = "Первый фильм",
                                ProductionYear = 2000
                            },
                            new KinopoiskFranchiseLibraryItem
                            {
                                ItemId = Guid.Parse("00000000-0000-0000-0000-000000000002"),
                                KinopoiskId = 101,
                                Name = "Продолжение",
                                ProductionYear = 2002
                            }
                        },
                        RelationTypes = new[]
                        {
                            FilmSequelsAndPrequelsResponseRelationType.SEQUEL
                        }
                    }
                }
            };
        }

        private static KinopoiskFranchiseReportOptions CreateOptions()
        {
            return new KinopoiskFranchiseReportOptions
            {
                IncludeSeries = true,
                IncludeSequels = true,
                IncludePrequels = true,
                IncludeRemakes = false,
                MinimumItems = 2,
                CollectionNameSuffix = " — коллекция",
                CollectionWritesEnabled = false,
                PreviewOnly = true
            };
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-franchise-report-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }
    }
}
