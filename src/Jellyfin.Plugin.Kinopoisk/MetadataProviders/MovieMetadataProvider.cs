using System.Net.Http;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class MovieMetadataProvider : BaseVideoMetadataProvider<Movie, MovieInfo>
    {
        public MovieMetadataProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<MovieInfo> providerIdResolver,
            ILogger<MovieMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : this(
                kinopoiskApiClient,
                kinopoiskApiClient as IKinopoiskDistributionApiClient
                    ?? EmptyKinopoiskDistributionApiClient.Instance,
                providerIdResolver,
                logger,
                httpClientFactory,
                null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public MovieMetadataProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IKinopoiskDistributionApiClient distributionApiClient,
            IProviderIdResolver<MovieInfo> providerIdResolver,
            ILogger<MovieMetadataProvider> logger,
            IHttpClientFactory httpClientFactory,
            IKinopoiskNativeTrailerCacheWarmupService trailerCacheWarmupService)
            : base(
                kinopoiskApiClient,
                distributionApiClient,
                providerIdResolver,
                logger,
                httpClientFactory,
                trailerCacheWarmupService)
        {
        }

        protected override Movie ConvertResponseToItem(Film apiResponse)
            => apiResponse.ToMovie();
    }
}
