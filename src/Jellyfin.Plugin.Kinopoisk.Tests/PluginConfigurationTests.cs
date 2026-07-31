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
            Assert.True(configuration.EnablePrecisePremiereDate);
            Assert.True(configuration.EnableRuntimeFallback);
            Assert.True(configuration.EnableKinopoiskHomePage);
            Assert.True(configuration.EnableSeriesStatus);
            Assert.True(configuration.EnableShortDescriptionFallback);
            Assert.True(configuration.EnablePersistentCache);
            Assert.True(configuration.UseStaleCacheOnFailure);
            Assert.True(configuration.EnableImageBinaryCache);
            Assert.True(configuration.UseStaleImageCacheOnFailure);
            Assert.True(configuration.EnableQuotaMonitoring);
            Assert.Equal(30, configuration.ImageBinaryCacheDays);
            Assert.Equal(2048, configuration.ImageBinaryCacheMaximumMegabytes);
            Assert.Equal(25, configuration.ImageBinaryCacheMaximumFileMegabytes);
            Assert.Equal(
                CommunityRatingSource.KinopoiskWithImdbFallback,
                configuration.CommunityRatingSource);
            Assert.Equal(
                CriticRatingSource.RussianWithWorldFallback,
                configuration.CriticRatingSource);
        }

        [Fact]
        public void ShouldNormalizeTokenCacheLimitsAndRatingSources()
        {
            var configuration = new PluginConfiguration
            {
                ApiToken = "  test-token  ",
                CommunityRatingSource = (CommunityRatingSource)999,
                CriticRatingSource = (CriticRatingSource)999,
                MetadataCacheHours = 0,
                ImagesCacheHours = 10000,
                SearchCacheMinutes = 0,
                NegativeCacheMinutes = 2000,
                PersistentCacheMaximumMegabytes = 1,
                ImageBinaryCacheDays = 0,
                ImageBinaryCacheMaximumMegabytes = 20000,
                ImageBinaryCacheMaximumFileMegabytes = 1000,
                QuotaCheckIntervalHours = 1000
            };

            configuration.Normalize();

            Assert.Equal("test-token", configuration.ApiToken);
            Assert.Equal(
                CommunityRatingSource.KinopoiskWithImdbFallback,
                configuration.CommunityRatingSource);
            Assert.Equal(
                CriticRatingSource.RussianWithWorldFallback,
                configuration.CriticRatingSource);
            Assert.Equal(1, configuration.MetadataCacheHours);
            Assert.Equal(8760, configuration.ImagesCacheHours);
            Assert.Equal(1, configuration.SearchCacheMinutes);
            Assert.Equal(1440, configuration.NegativeCacheMinutes);
            Assert.Equal(64, configuration.PersistentCacheMaximumMegabytes);
            Assert.Equal(1, configuration.ImageBinaryCacheDays);
            Assert.Equal(16384, configuration.ImageBinaryCacheMaximumMegabytes);
            Assert.Equal(100, configuration.ImageBinaryCacheMaximumFileMegabytes);
            Assert.Equal(168, configuration.QuotaCheckIntervalHours);
        }
    }
}
