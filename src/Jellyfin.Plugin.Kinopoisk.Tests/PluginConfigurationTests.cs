using System;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Services;
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
            Assert.True(configuration.EnableYoutubeTrailerFallback);
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
            Assert.True(configuration.EnableNativeTrailerCache);
            Assert.Equal(
                KinopoiskTrailerCachePopulationMode.OnDemand,
                configuration.NativeTrailerCachePopulationMode);
            Assert.Equal(
                KinopoiskTrailerCacheClientScope.AllClients,
                configuration.NativeTrailerCacheClientScope);
            Assert.Equal(25, configuration.NativeTrailerCachePlaybackWaitSeconds);
            Assert.True(configuration.EnableQuotaMonitoring);
            Assert.False(configuration.EnableDiagnosticMode);
            Assert.Equal(DiagnosticLogLevel.Detailed, configuration.DiagnosticLogLevel);
            Assert.Equal(6, configuration.DiagnosticSessionHours);
            Assert.Equal(25, configuration.DiagnosticMaximumFileMegabytes);
            Assert.Equal(5, configuration.DiagnosticRetentionFiles);
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
                DiagnosticLogLevel = (KinopoiskDiagnosticLevel)999,
                MetadataCacheHours = 0,
                ImagesCacheHours = 10000,
                SearchCacheMinutes = 0,
                NegativeCacheMinutes = 2000,
                PersistentCacheMaximumMegabytes = 1,
                ImageBinaryCacheDays = 0,
                ImageBinaryCacheMaximumMegabytes = 20000,
                ImageBinaryCacheMaximumFileMegabytes = 1000,
                NativeTrailerCachePopulationMode = (KinopoiskTrailerCachePopulationMode)999,
                NativeTrailerCacheClientScope = (KinopoiskTrailerCacheClientScope)999,
                NativeTrailerCachePlaybackWaitSeconds = 1000,
                QuotaCheckIntervalHours = 1000,
                DiagnosticSessionHours = 100,
                DiagnosticMaximumFileMegabytes = 1,
                DiagnosticRetentionFiles = 100
            };

            configuration.Normalize();

            Assert.Equal("test-token", configuration.ApiToken);
            Assert.Equal(
                CommunityRatingSource.KinopoiskWithImdbFallback,
                configuration.CommunityRatingSource);
            Assert.Equal(
                CriticRatingSource.RussianWithWorldFallback,
                configuration.CriticRatingSource);
            Assert.Equal(DiagnosticLogLevel.Detailed, configuration.DiagnosticLogLevel);
            Assert.Equal(1, configuration.MetadataCacheHours);
            Assert.Equal(8760, configuration.ImagesCacheHours);
            Assert.Equal(1, configuration.SearchCacheMinutes);
            Assert.Equal(1440, configuration.NegativeCacheMinutes);
            Assert.Equal(64, configuration.PersistentCacheMaximumMegabytes);
            Assert.Equal(1, configuration.ImageBinaryCacheDays);
            Assert.Equal(16384, configuration.ImageBinaryCacheMaximumMegabytes);
            Assert.Equal(100, configuration.ImageBinaryCacheMaximumFileMegabytes);
            Assert.Equal(
                KinopoiskTrailerCachePopulationMode.OnDemand,
                configuration.NativeTrailerCachePopulationMode);
            Assert.Equal(
                KinopoiskTrailerCacheClientScope.AllClients,
                configuration.NativeTrailerCacheClientScope);
            Assert.Equal(60, configuration.NativeTrailerCachePlaybackWaitSeconds);
            Assert.Equal(168, configuration.QuotaCheckIntervalHours);
            Assert.Equal(24, configuration.DiagnosticSessionHours);
            Assert.Equal(5, configuration.DiagnosticMaximumFileMegabytes);
            Assert.Equal(20, configuration.DiagnosticRetentionFiles);
        }

        [Fact]
        public void ShouldCreateBoundedDiagnosticSession()
        {
            var before = DateTimeOffset.UtcNow;
            var configuration = new PluginConfiguration
            {
                EnableDiagnosticMode = true,
                DiagnosticSessionHours = 3,
                DiagnosticSessionId = string.Empty,
                DiagnosticSessionStartedUtc = null,
                DiagnosticSessionExpiresUtc = null
            };

            configuration.Normalize();

            Assert.True(configuration.EnableDiagnosticMode);
            Assert.StartsWith("diag-", configuration.DiagnosticSessionId);
            Assert.NotNull(configuration.DiagnosticSessionStartedUtc);
            Assert.NotNull(configuration.DiagnosticSessionExpiresUtc);
            Assert.True(configuration.DiagnosticSessionStartedUtc >= before);
            Assert.Equal(
                TimeSpan.FromHours(3),
                configuration.DiagnosticSessionExpiresUtc.Value
                    - configuration.DiagnosticSessionStartedUtc.Value);
        }

        [Fact]
        public void ShouldDisableExpiredDiagnosticSession()
        {
            var configuration = new PluginConfiguration
            {
                EnableDiagnosticMode = true,
                DiagnosticSessionId = "diag-expired",
                DiagnosticSessionStartedUtc = DateTimeOffset.UtcNow.AddHours(-2),
                DiagnosticSessionExpiresUtc = DateTimeOffset.UtcNow.AddMinutes(-1)
            };

            configuration.Normalize();

            Assert.False(configuration.EnableDiagnosticMode);
            Assert.Equal("diag-expired", configuration.DiagnosticSessionId);
        }
    }
}
