using System;
using System.Xml.Serialization;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Model.Plugins;

namespace Jellyfin.Plugin.Kinopoisk.Configuration
{
    public class PluginConfiguration : BasePluginConfiguration
    {
        public string ApiToken { get; set; } = string.Empty;

        public bool EnableWebBootstrap { get; set; } = true;

        public bool EnableMovieMetadata { get; set; } = true;

        public bool EnableSeriesMetadata { get; set; } = true;

        public bool EnableSeasonEpisodeMetadata { get; set; } = true;

        public bool EnablePeopleMetadata { get; set; } = true;

        public bool EnableTrailers { get; set; } = true;

        public int MaximumTrailers { get; set; } = 5;

        public bool PreferOfficialTrailers { get; set; } = true;

        public bool PreferRussianTrailers { get; set; } = true;

        public bool IncludeTrailerTeasers { get; set; } = true;

        public bool IncludeAdditionalTrailerVideos { get; set; }

        public bool PrefixTrailerNames { get; set; } = true;

        public bool EnableYoutubeTrailerFallback { get; set; } = true;

        public bool EnableImages { get; set; } = true;

        public CommunityRatingSource CommunityRatingSource { get; set; }
            = CommunityRatingSource.KinopoiskWithImdbFallback;

        public CriticRatingSource CriticRatingSource { get; set; }
            = CriticRatingSource.RussianWithWorldFallback;

        public bool EnablePrecisePremiereDate { get; set; } = true;

        public bool EnableRuntimeFallback { get; set; } = true;

        public bool EnableKinopoiskHomePage { get; set; } = true;

        public bool EnableSeriesStatus { get; set; } = true;

        public bool EnableShortDescriptionFallback { get; set; } = true;

        public bool EnableFranchiseCollections { get; set; }

        public bool FranchisePreviewOnly { get; set; } = true;

        public bool IncludeSeriesInFranchises { get; set; } = true;

        public bool IncludeFranchiseSequels { get; set; } = true;

        public bool IncludeFranchisePrequels { get; set; } = true;

        public bool IncludeFranchiseRemakes { get; set; }

        public int MinimumFranchiseItems { get; set; } = 2;

        public string FranchiseCollectionNameSuffix { get; set; } = " — коллекция";

        public bool PreserveManualCollections { get; set; } = true;

        public bool RemoveMissingItemsFromManagedCollections { get; set; }

        public bool EnablePersistentCache { get; set; } = true;

        public bool UseStaleCacheOnFailure { get; set; } = true;

        public int MetadataCacheHours { get; set; } = 168;

        public int ImagesCacheHours { get; set; } = 720;

        public int SearchCacheMinutes { get; set; } = 60;

        public int NegativeCacheMinutes { get; set; } = 15;

        public int PersistentCacheMaximumMegabytes { get; set; } = 512;

        public bool EnableImageBinaryCache { get; set; } = true;

        public bool UseStaleImageCacheOnFailure { get; set; } = true;

        public int ImageBinaryCacheDays { get; set; } = 30;

        public int ImageBinaryCacheMaximumMegabytes { get; set; } = 2048;

        public int ImageBinaryCacheMaximumFileMegabytes { get; set; } = 25;

        public bool EnableNativeTrailerCache { get; set; } = true;

        public KinopoiskTrailerCachePopulationMode NativeTrailerCachePopulationMode { get; set; }
            = KinopoiskTrailerCachePopulationMode.OnDemand;

        public KinopoiskTrailerCacheClientScope NativeTrailerCacheClientScope { get; set; }
            = KinopoiskTrailerCacheClientScope.AllClients;

        public int NativeTrailerCachePlaybackWaitSeconds { get; set; } = 25;

        public int NativeTrailerCacheMaximumMegabytes { get; set; } = 4096;

        public int NativeTrailerCacheRetentionDays { get; set; } = 30;

        public int NativeTrailerCacheMaximumFileMegabytes { get; set; } = 512;

        public bool EnableQuotaMonitoring { get; set; } = true;

        public int QuotaCheckIntervalHours { get; set; } = 6;

        public bool EnableDiagnosticMode { get; set; }

        public KinopoiskDiagnosticLevel DiagnosticLogLevel { get; set; }
            = KinopoiskDiagnosticLevel.Detailed;

        public int DiagnosticSessionHours { get; set; } = 6;

        public int DiagnosticMaximumFileMegabytes { get; set; } = 25;

        public int DiagnosticRetentionFiles { get; set; } = 5;

        public string DiagnosticSessionId { get; set; } = string.Empty;

        public DateTimeOffset? DiagnosticSessionStartedUtc { get; set; }

        public DateTimeOffset? DiagnosticSessionExpiresUtc { get; set; }

        [XmlIgnore]
        public KinopoiskDiagnosticsSnapshot Diagnostics
            => KinopoiskDiagnostics.Shared.GetSnapshot();

        [XmlIgnore]
        public KinopoiskWebBootstrapSnapshot WebBootstrap
            => KinopoiskWebBootstrapState.GetSnapshot(EnableWebBootstrap);

        public void Normalize()
        {
            ApiToken = ApiToken?.Trim() ?? string.Empty;
            FranchiseCollectionNameSuffix = string.IsNullOrWhiteSpace(FranchiseCollectionNameSuffix)
                ? " — коллекция"
                : FranchiseCollectionNameSuffix.TrimEnd();

            if (!Enum.IsDefined(CommunityRatingSource))
                CommunityRatingSource = CommunityRatingSource.KinopoiskWithImdbFallback;

            if (!Enum.IsDefined(CriticRatingSource))
                CriticRatingSource = CriticRatingSource.RussianWithWorldFallback;

            if (!Enum.IsDefined(DiagnosticLogLevel))
                DiagnosticLogLevel = KinopoiskDiagnosticLevel.Detailed;

            if (!Enum.IsDefined(NativeTrailerCachePopulationMode))
                NativeTrailerCachePopulationMode = KinopoiskTrailerCachePopulationMode.OnDemand;

            if (!Enum.IsDefined(NativeTrailerCacheClientScope))
                NativeTrailerCacheClientScope = KinopoiskTrailerCacheClientScope.AllClients;

            MaximumTrailers = Math.Clamp(MaximumTrailers, 1, 20);
            MinimumFranchiseItems = Math.Clamp(MinimumFranchiseItems, 2, 1000);
            MetadataCacheHours = Math.Clamp(MetadataCacheHours, 1, 8760);
            ImagesCacheHours = Math.Clamp(ImagesCacheHours, 1, 8760);
            SearchCacheMinutes = Math.Clamp(SearchCacheMinutes, 1, 10080);
            NegativeCacheMinutes = Math.Clamp(NegativeCacheMinutes, 1, 1440);
            PersistentCacheMaximumMegabytes = Math.Clamp(
                PersistentCacheMaximumMegabytes,
                64,
                8192);
            ImageBinaryCacheDays = Math.Clamp(ImageBinaryCacheDays, 1, 3650);
            ImageBinaryCacheMaximumMegabytes = Math.Clamp(
                ImageBinaryCacheMaximumMegabytes,
                64,
                16384);
            ImageBinaryCacheMaximumFileMegabytes = Math.Clamp(
                ImageBinaryCacheMaximumFileMegabytes,
                1,
                100);
            NativeTrailerCacheMaximumMegabytes = Math.Clamp(
                NativeTrailerCacheMaximumMegabytes,
                256,
                16384);
            NativeTrailerCacheRetentionDays = Math.Clamp(
                NativeTrailerCacheRetentionDays,
                7,
                365);
            NativeTrailerCacheMaximumFileMegabytes = Math.Clamp(
                NativeTrailerCacheMaximumFileMegabytes,
                64,
                2048);
            NativeTrailerCachePlaybackWaitSeconds = Math.Clamp(
                NativeTrailerCachePlaybackWaitSeconds,
                0,
                60);
            QuotaCheckIntervalHours = Math.Clamp(QuotaCheckIntervalHours, 1, 168);
            DiagnosticSessionHours = Math.Clamp(DiagnosticSessionHours, 1, 24);
            DiagnosticMaximumFileMegabytes = Math.Clamp(
                DiagnosticMaximumFileMegabytes,
                5,
                200);
            DiagnosticRetentionFiles = Math.Clamp(DiagnosticRetentionFiles, 1, 20);

            if (!EnableFranchiseCollections)
                FranchisePreviewOnly = true;

            PreserveManualCollections = true;

            if (!EnableDiagnosticMode)
                return;

            var now = DateTimeOffset.UtcNow;
            if (DiagnosticSessionExpiresUtc.HasValue
                && DiagnosticSessionExpiresUtc.Value <= now
                && !string.IsNullOrWhiteSpace(DiagnosticSessionId))
            {
                EnableDiagnosticMode = false;
                return;
            }

            if (string.IsNullOrWhiteSpace(DiagnosticSessionId)
                || !DiagnosticSessionStartedUtc.HasValue
                || !DiagnosticSessionExpiresUtc.HasValue)
            {
                DiagnosticSessionId = $"diag-{now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}"[..29];
                DiagnosticSessionStartedUtc = now;
                DiagnosticSessionExpiresUtc = now.AddHours(DiagnosticSessionHours);
            }
        }
    }
}
