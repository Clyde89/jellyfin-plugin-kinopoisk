using System;
using System.IO;
using System.Net.Http;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk
{
    /// <summary>
    /// Регистрирует сервисы плагина.
    /// </summary>
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
            serviceCollection.AddHostedService<KinopoiskQuotaMonitor>();

            serviceCollection.AddSingleton<IProviderIdResolver<MovieInfo>, VideoResolver<MovieInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<SeriesInfo>, VideoResolver<SeriesInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<PersonLookupInfo>, CommonResolver<PersonLookupInfo>>();
            serviceCollection.AddSingleton<IProviderIdResolver<BaseItem>, CommonResolver<BaseItem>>();
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
    }
}
