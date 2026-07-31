using Jellyfin.Plugin.Kinopoisk.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PluginConfigurationTests
    {
        [Fact]
        public void ShouldNotContainDefaultApiToken()
        {
            var configuration = new PluginConfiguration();

            Assert.True(string.IsNullOrWhiteSpace(configuration.ApiToken));
        }

        [Fact]
        public void ShouldEnableSelfSufficientProviderFunctionsByDefault()
        {
            var configuration = new PluginConfiguration();

            Assert.True(configuration.EnableMovieMetadata);
            Assert.True(configuration.EnableSeriesMetadata);
            Assert.True(configuration.EnableSeasonEpisodeMetadata);
            Assert.True(configuration.EnablePeopleMetadata);
            Assert.True(configuration.EnableTrailers);
            Assert.True(configuration.EnableImages);
            Assert.True(configuration.EnablePersistentCache);
            Assert.True(configuration.UseStaleCacheOnFailure);
            Assert.True(configuration.EnableQuotaMonitoring);
        }

        [Fact]
        public void ShouldNormalizeTokenAndCacheLimits()
        {
            var configuration = new PluginConfiguration
            {
                ApiToken = "  test-token  ",
                MetadataCacheHours = 0,
                ImagesCacheHours = 10000,
                SearchCacheMinutes = 0,
                NegativeCacheMinutes = 2000,
                PersistentCacheMaximumMegabytes = 1,
                QuotaCheckIntervalHours = 1000
            };

            configuration.Normalize();

            Assert.Equal("test-token", configuration.ApiToken);
            Assert.Equal(1, configuration.MetadataCacheHours);
            Assert.Equal(8760, configuration.ImagesCacheHours);
            Assert.Equal(1, configuration.SearchCacheMinutes);
            Assert.Equal(1440, configuration.NegativeCacheMinutes);
            Assert.Equal(64, configuration.PersistentCacheMaximumMegabytes);
            Assert.Equal(168, configuration.QuotaCheckIntervalHours);
        }
    }
}
