using System;
using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchisePlanFingerprintTests
    {
        [Fact]
        public void ShouldRemainStableForDifferentInputOrder()
        {
            var options = Options();
            var first = KinopoiskFranchisePlanFingerprint.Compute(
                new[]
                {
                    Plan(200, Item(2, 201), Item(1, 200)),
                    Plan(100, Item(4, 101), Item(3, 100))
                },
                options);
            var second = KinopoiskFranchisePlanFingerprint.Compute(
                new[]
                {
                    Plan(100, Item(3, 100), Item(4, 101)),
                    Plan(200, Item(1, 200), Item(2, 201))
                },
                options);

            Assert.Equal(first, second);
            Assert.Matches("^[0-9a-f]{64}$", first);
        }

        [Fact]
        public void ShouldChangeWhenLocalMembershipChanges()
        {
            var options = Options();
            var first = KinopoiskFranchisePlanFingerprint.Compute(
                new[] { Plan(100, Item(1, 100), Item(2, 101)) },
                options);
            var second = KinopoiskFranchisePlanFingerprint.Compute(
                new[] { Plan(100, Item(1, 100), Item(3, 102)) },
                options);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void ShouldChangeWhenRelevantOptionsChange()
        {
            var plans = new[] { Plan(100, Item(1, 100), Item(2, 101)) };
            var firstOptions = Options();
            var secondOptions = Options();
            secondOptions.IncludeRemakes = true;

            var first = KinopoiskFranchisePlanFingerprint.Compute(plans, firstOptions);
            var second = KinopoiskFranchisePlanFingerprint.Compute(plans, secondOptions);

            Assert.NotEqual(first, second);
        }

        [Fact]
        public void ShouldIgnoreWriteModeFlags()
        {
            var plans = new[] { Plan(100, Item(1, 100), Item(2, 101)) };
            var previewOptions = Options();
            previewOptions.CollectionWritesEnabled = false;
            previewOptions.PreviewOnly = true;
            var applyOptions = Options();
            applyOptions.CollectionWritesEnabled = true;
            applyOptions.PreviewOnly = false;

            var preview = KinopoiskFranchisePlanFingerprint.Compute(plans, previewOptions);
            var apply = KinopoiskFranchisePlanFingerprint.Compute(plans, applyOptions);

            Assert.Equal(preview, apply);
        }

        private static KinopoiskFranchiseReportOptions Options()
            => new()
            {
                IncludeSeries = true,
                IncludeSequels = true,
                IncludePrequels = true,
                IncludeRemakes = false,
                MinimumItems = 2,
                CollectionNameSuffix = " — коллекция"
            };

        private static KinopoiskFranchisePlan Plan(
            int anchorId,
            params KinopoiskFranchiseLibraryItem[] items)
            => new()
            {
                AnchorKinopoiskId = anchorId,
                SuggestedName = $"Франшиза {anchorId}",
                Items = items
            };

        private static KinopoiskFranchiseLibraryItem Item(int localId, int kinopoiskId)
            => new()
            {
                ItemId = Guid.Parse($"00000000-0000-0000-0000-{localId:D12}"),
                KinopoiskId = kinopoiskId,
                Name = $"Фильм {kinopoiskId}",
                ProductionYear = 2000 + localId
            };
    }
}
