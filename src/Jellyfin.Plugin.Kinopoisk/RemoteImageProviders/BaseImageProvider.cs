using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Common.Net;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public abstract class BaseImageProvider : IRemoteImageProvider
    {
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly KinopoiskImageBinaryCache _imageBinaryCache;

        protected BaseImageProvider(IHttpClientFactory httpClientFactory)
            : this(httpClientFactory, null)
        {
        }

        protected BaseImageProvider(
            IHttpClientFactory httpClientFactory,
            KinopoiskImageBinaryCache imageBinaryCache)
        {
            _httpClientFactory = httpClientFactory ?? throw new ArgumentNullException(nameof(httpClientFactory));
            _imageBinaryCache = imageBinaryCache;
        }

        public abstract string Name { get; }

        public abstract bool Supports(BaseItem item);

        public Task<HttpResponseMessage> GetImageResponse(
            string url,
            CancellationToken cancellationToken)
        {
            return _imageBinaryCache is null
                ? _httpClientFactory.CreateClient(NamedClient.Default).GetAsync(
                    url,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                : _imageBinaryCache.GetImageResponse(url, cancellationToken);
        }

        public abstract Task<IEnumerable<RemoteImageInfo>> GetImages(
            BaseItem item,
            CancellationToken cancellationToken);

        public abstract IEnumerable<ImageType> GetSupportedImages(BaseItem item);

        protected async Task<IEnumerable<RemoteImageInfo>> FilterEmptyImages(
            IEnumerable<RemoteImageInfo> images)
        {
            using var httpClient = new HttpClient(
                new HttpClientHandler { AllowAutoRedirect = false },
                true);
            var sanitizer = new RemoteImageUrlSanitizer(httpClient);
            var result = await Task.WhenAll(images.Select(async image =>
            {
                if (image is null)
                    return null;

                var sanitizedUrl = await sanitizer
                    .SanitizeRemoteImageUrl(image.Url)
                    .ConfigureAwait(false);
                if (string.IsNullOrEmpty(sanitizedUrl))
                    return null;

                image.Url = sanitizedUrl;
                return image;
            })).ConfigureAwait(false);

            return result.Where(image => image is not null).ToArray();
        }
    }
}
