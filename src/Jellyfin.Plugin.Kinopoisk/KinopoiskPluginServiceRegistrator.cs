using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Kinopoisk.Presentation;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk
{
    public class KinopoiskPluginServiceRegistrator : IPluginServiceRegistrator
    {
        public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
        {
            serviceCollection.AddSingleton(KinopoiskDiagnostics.Shared);
            serviceCollection.AddSingleton((sp) => new KinopoiskApiClient(
                Plugin.Instance.Configuration.ApiToken,
                sp.GetRequiredService<ILogger<KinopoiskApiClient>>(),
                sp.GetRequiredService<IHttpClientFactory>()
            ));
            serviceCollection.AddSingleton<IKinopoiskQuotaApiClient>((sp) =>
                sp.GetRequiredService<KinopoiskApiClient>());
            serviceCollection.AddSingleton((sp) => new CachedKinopoiskApiClient(
                sp.GetRequiredService<KinopoiskApiClient>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<ILogger<CachedKinopoiskApiClient>>(),
                CreateCacheOptions(),
                sp.GetRequiredService<KinopoiskDiagnostics>()
            ));
            serviceCollection.AddSingleton<IKinopoiskApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskApiClient>());
            serviceCollection.AddSingleton<IKinopoiskImageApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskApiClient>());
            serviceCollection.AddSingleton<IKinopoiskPersonSearchApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskApiClient>());
            serviceCollection.AddSingleton<IKinopoiskSeasonApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskApiClient>());
            serviceCollection.AddSingleton<IKinopoiskDistributionApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskApiClient>());

            serviceCollection.AddSingleton((sp) => new KinopoiskRelationsApiClient(
                Plugin.Instance.Configuration.ApiToken,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<ILogger<KinopoiskRelationsApiClient>>()
            ));
            serviceCollection.AddSingleton((sp) => new CachedKinopoiskRelationsApiClient(
                sp.GetRequiredService<KinopoiskRelationsApiClient>(),
                sp.GetRequiredService<IMemoryCache>(),
                CreateCacheOptions(),
                sp.GetRequiredService<KinopoiskDiagnostics>(),
                sp.GetRequiredService<ILogger<CachedKinopoiskRelationsApiClient>>()
            ));
            serviceCollection.AddSingleton<IKinopoiskRelationsApiClient>((sp) =>
                sp.GetRequiredService<CachedKinopoiskRelationsApiClient>());

            serviceCollection.AddSingleton<KinopoiskPresentationService>();
            serviceCollection.AddSingleton((sp) => new KinopoiskSupplementalApiClient(
                Plugin.Instance.Configuration.ApiToken,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<KinopoiskDiagnostics>(),
                sp.GetRequiredService<ILogger<KinopoiskSupplementalApiClient>>()
            ));
            serviceCollection.AddSingleton((sp) => new KinopoiskSimilarApiClient(
                Plugin.Instance.Configuration.ApiToken,
                sp.GetRequiredService<IHttpClientFactory>(),
                sp.GetRequiredService<IMemoryCache>(),
                sp.GetRequiredService<KinopoiskDiagnostics>(),
                sp.GetRequiredService<ILogger<KinopoiskSimilarApiClient>>()
            ));

            serviceCollection.AddSingleton<KinopoiskFranchisePlanner>();
            serviceCollection.AddSingleton<KinopoiskFranchisePreviewService>();
            serviceCollection.AddSingleton<KinopoiskFranchiseLibraryScanner>();
            serviceCollection.AddSingleton((sp) => new KinopoiskFranchiseReportWriter(
                GetReportsPath(),
                sp.GetRequiredService<ILogger<KinopoiskFranchiseReportWriter>>()
            ));
            serviceCollection.AddSingleton(_ => new KinopoiskFranchisePreviewReportReader(
                GetReportsPath()));
            serviceCollection.AddSingleton<IKinopoiskManagedCollectionGateway, KinopoiskManagedCollectionGateway>();
            serviceCollection.AddSingleton<KinopoiskFranchiseApplyService>();
            serviceCollection.AddSingleton((sp) => new KinopoiskFranchiseApplyReportWriter(
                GetReportsPath(),
                sp.GetRequiredService<ILogger<KinopoiskFranchiseApplyReportWriter>>()
            ));

            serviceCollection.AddSingleton((sp) => new KinopoiskImageBinaryCache(
                sp.GetRequiredService<IHttpClientFactory>(),
                CreateImageCacheOptions(),
                sp.GetRequiredService<KinopoiskDiagnostics>(),
                sp.GetRequiredService<ILogger<KinopoiskImageBinaryCache>>()
            ));
            serviceCollection.AddHostedService<KinopoiskQuotaMonitor>();
            serviceCollection.AddHostedService<KinopoiskWebTrailerIntegrationService>();
            serviceCollection.AddHostedService<KinopoiskWebPresentationIntegrationService>();
            serviceCollection.AddHostedService<KinopoiskWebReviewsIntegrationService>();
            serviceCollection.AddHostedService<KinopoiskWebTagLocalizationService>();
            serviceCollection.AddHostedService<KinopoiskWebElsewhereBridgeService>();
            serviceCollection.AddHostedService<KinopoiskWebRecommendationsService>();

            serviceCollection.AddSingleton<IProviderIdResolver<MovieInfo>, VideoResolver<MovieInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<SeriesInfo>, VideoResolver<SeriesInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<PersonLookupInfo>, CommonResolver<PersonLookupInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<BaseItem>, CommonResolver<BaseItem>>();
        }

        private static string GetReportsPath()
        {
            var plugin = Plugin.Instance
                ?? throw new InvalidOperationException("Экземпляр плагина КиноПоиск ещё не создан.");
            return Path.Combine(plugin.DataFolderPath, "reports");
        }

        private static KinopoiskCacheOptions CreateCacheOptions()
        {
            var configuration = Plugin.Instance.Configuration;
            var maximumMegabytes = Math.Clamp(
                configuration.PersistentCacheMaximumMegabytes,
                64,
                8192);

            return new KinopoiskCacheOptions
            {
                EnablePersistentCache = configuration.EnablePersistentCache,
                UseStaleCacheOnFailure = configuration.UseStaleCacheOnFailure,
                PersistentCachePath = Path.Combine(
                    Plugin.Instance.DataFolderPath,
                    "cache",
                    "api"),
                MetadataExpiration = TimeSpan.FromHours(
                    Math.Clamp(configuration.MetadataCacheHours, 1, 8760)),
                ImagesExpiration = TimeSpan.FromHours(
                    Math.Clamp(configuration.ImagesCacheHours, 1, 8760)),
                SearchExpiration = TimeSpan.FromMinutes(
                    Math.Clamp(configuration.SearchCacheMinutes, 1, 10080)),
                EmptyResultExpiration = TimeSpan.FromMinutes(
                    Math.Clamp(configuration.NegativeCacheMinutes, 1, 1440)),
                MaximumPersistentCacheBytes = maximumMegabytes * 1024L * 1024L
            };
        }

        private static KinopoiskImageCacheOptions CreateImageCacheOptions()
        {
            var configuration = Plugin.Instance.Configuration;
            return new KinopoiskImageCacheOptions
            {
                Enabled = configuration.EnableImageBinaryCache,
                UseStaleOnFailure = configuration.UseStaleImageCacheOnFailure,
                CachePath = Path.Combine(
                    Plugin.Instance.DataFolderPath,
                    "cache",
                    "images"),
                Expiration = TimeSpan.FromDays(
                    Math.Clamp(configuration.ImageBinaryCacheDays, 1, 3650)),
                MaximumCacheBytes = Math.Clamp(
                        configuration.ImageBinaryCacheMaximumMegabytes,
                        64,
                        16384)
                    * 1024L * 1024L,
                MaximumFileBytes = Math.Clamp(
                        configuration.ImageBinaryCacheMaximumFileMegabytes,
                        1,
                        100)
                    * 1024L * 1024L
            };
        }
    }
}
