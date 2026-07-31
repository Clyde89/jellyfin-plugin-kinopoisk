using Jellyfin.Plugin.Kinopoisk.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class FranchiseConfigurationTests
    {
        [Fact]
        public void ShouldUseSafeFranchiseDefaults()
        {
            var configuration = new PluginConfiguration();

            Assert.False(configuration.EnableFranchiseCollections);
            Assert.True(configuration.FranchisePreviewOnly);
            Assert.True(configuration.IncludeSeriesInFranchises);
            Assert.True(configuration.IncludeFranchiseSequels);
            Assert.True(configuration.IncludeFranchisePrequels);
            Assert.False(configuration.IncludeFranchiseRemakes);
            Assert.Equal(2, configuration.MinimumFranchiseItems);
            Assert.Equal(" — коллекция", configuration.FranchiseCollectionNameSuffix);
            Assert.True(configuration.PreserveManualCollections);
            Assert.False(configuration.RemoveMissingItemsFromManagedCollections);
        }

        [Theory]
        [InlineData(-1, 2)]
        [InlineData(1, 2)]
        [InlineData(2, 2)]
        [InlineData(1000, 1000)]
        [InlineData(5000, 1000)]
        public void ShouldNormalizeMinimumFranchiseItems(int value, int expected)
        {
            var configuration = new PluginConfiguration
            {
                MinimumFranchiseItems = value
            };

            configuration.Normalize();

            Assert.Equal(expected, configuration.MinimumFranchiseItems);
        }

        [Fact]
        public void ShouldForcePreviewWhenCollectionWritesAreDisabled()
        {
            var configuration = new PluginConfiguration
            {
                EnableFranchiseCollections = false,
                FranchisePreviewOnly = false
            };

            configuration.Normalize();

            Assert.True(configuration.FranchisePreviewOnly);
        }

        [Fact]
        public void ShouldPreserveManualCollectionsRegardlessOfInvalidConfiguration()
        {
            var configuration = new PluginConfiguration
            {
                PreserveManualCollections = false
            };

            configuration.Normalize();

            Assert.True(configuration.PreserveManualCollections);
        }

        [Fact]
        public void ShouldRestoreDefaultCollectionSuffix()
        {
            var configuration = new PluginConfiguration
            {
                FranchiseCollectionNameSuffix = "   "
            };

            configuration.Normalize();

            Assert.Equal(" — коллекция", configuration.FranchiseCollectionNameSuffix);
        }
    }
}
