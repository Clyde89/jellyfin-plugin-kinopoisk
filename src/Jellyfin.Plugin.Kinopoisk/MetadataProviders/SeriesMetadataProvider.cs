using System.Net.Http;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class SeriesMetadataProvider : BaseVideoMetadataProvider<Series, SeriesInfo>
    {
        public SeriesMetadataProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<SeriesInfo> providerIdResolver,
            ILogger<SeriesMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : this(
                kinopoiskApiClient,
                kinopoiskApiClient as IKinopoiskDistributionApiClient
                    ?? EmptyKinopoiskDistributionApiClient.Instance,
                providerIdResolver,
                logger,
                httpClientFactory)
        {
        }

        [ActivatorUtilitiesConstructor]
        public SeriesMetadataProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IKinopoiskDistributionApiClient distributionApiClient,
            IProviderIdResolver<SeriesInfo> providerIdResolver,
            ILogger<SeriesMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : base(
                kinopoiskApiClient,
                distributionApiClient,
                providerIdResolver,
                logger,
                httpClientFactory)
        {
        }

        protected override Series ConvertResponseToItem(Film apiResponse)
            => apiResponse.ToSeries();
    }
}
