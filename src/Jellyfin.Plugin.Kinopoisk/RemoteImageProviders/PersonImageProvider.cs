using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class PersonImageProvider : BaseImageProvider
    {
        private readonly IKinopoiskApiClient _apiClient;
        private readonly IProviderIdResolver<BaseItem> _providerIdResolver;
        private readonly ILogger<PersonImageProvider> _logger;

        public PersonImageProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<BaseItem> providerIdResolver,
            ILogger<PersonImageProvider> logger,
            IHttpClientFactory httpClientFactory)
            : this(
                kinopoiskApiClient,
                providerIdResolver,
                logger,
                httpClientFactory,
                null)
        {
        }

        public PersonImageProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<BaseItem> providerIdResolver,
            ILogger<PersonImageProvider> logger,
            IHttpClientFactory httpClientFactory,
            KinopoiskImageBinaryCache imageBinaryCache)
            : base(httpClientFactory, imageBinaryCache)
        {
            _apiClient = kinopoiskApiClient ?? throw new ArgumentNullException(nameof(kinopoiskApiClient));
            _providerIdResolver = providerIdResolver ?? throw new ArgumentNullException(nameof(providerIdResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public override string Name => Constants.ProviderName;

        public override bool Supports(BaseItem item)
            => Plugin.Instance?.Configuration.EnableImages != false && item is Person;

        public override async Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item,
            CancellationToken cancellationToken)
        {
            if (Plugin.Instance?.Configuration.EnableImages == false)
                return Enumerable.Empty<RemoteImageInfo>();

            var (resolveResult, kinopoiskId) = await _providerIdResolver
                .TryResolve(item, cancellationToken)
                .ConfigureAwait(false);
            if (!resolveResult)
                return Enumerable.Empty<RemoteImageInfo>();

            var person = await _apiClient
                .GetPerson(kinopoiskId, cancellationToken)
                .ConfigureAwait(false);

            var images = new[] { person.ToRemoteImageInfo() };
            return await FilterEmptyImages(images).ConfigureAwait(false);
        }

        public override IEnumerable<ImageType> GetSupportedImages(BaseItem item)
        {
            if (Plugin.Instance?.Configuration.EnableImages != false)
                yield return ImageType.Primary;
        }
    }
}
