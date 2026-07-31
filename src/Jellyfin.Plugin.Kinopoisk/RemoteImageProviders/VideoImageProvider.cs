using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class VideoImageProvider : BaseImageProvider
    {
        private static readonly (FilmImageType SourceType, ImageType TargetType)[] ImageMappings =
        {
            (FilmImageType.POSTER, ImageType.Primary),
            (FilmImageType.COVER, ImageType.Primary),
            (FilmImageType.FAN_ART, ImageType.Backdrop),
            (FilmImageType.WALLPAPER, ImageType.Backdrop),
            (FilmImageType.PROMO, ImageType.Backdrop),
            (FilmImageType.CONCEPT, ImageType.Backdrop),
            (FilmImageType.STILL, ImageType.Screenshot),
            (FilmImageType.SCREENSHOT, ImageType.Screenshot),
            (FilmImageType.SHOOTING, ImageType.Screenshot)
        };

        private readonly ILogger<VideoImageProvider> _logger;
        private readonly IKinopoiskApiClient _apiClient;
        private readonly IKinopoiskImageApiClient _imageApiClient;
        private readonly IProviderIdResolver<BaseItem> _providerIdResolver;

        public override string Name => Constants.ProviderName;

        public VideoImageProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<BaseItem> providerIdResolver,
            ILogger<VideoImageProvider> logger,
            IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiClient = kinopoiskApiClient ?? throw new ArgumentNullException(nameof(kinopoiskApiClient));
            _imageApiClient = kinopoiskApiClient as IKinopoiskImageApiClient;
            _providerIdResolver = providerIdResolver ?? throw new ArgumentNullException(nameof(providerIdResolver));
        }

        public override bool Supports(BaseItem item)
            => Plugin.Instance?.Configuration.EnableImages != false
                && (item is Movie || item is Series);

        public override IEnumerable<ImageType> GetSupportedImages(BaseItem item)
            => Plugin.Instance?.Configuration.EnableImages == false
                ? Enumerable.Empty<ImageType>()
                : new[]
                {
                    ImageType.Primary,
                    ImageType.Backdrop,
                    ImageType.Screenshot
                };

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

            var film = await _apiClient
                .GetSingleFilm(kinopoiskId, cancellationToken)
                .ConfigureAwait(false);
            var baseImages = await FilterEmptyImages(film.ToRemoteImageInfos())
                .ConfigureAwait(false);

            if (_imageApiClient is null)
                return baseImages;

            var requests = ImageMappings.Select(mapping =>
                GetImagesSafely(
                    kinopoiskId,
                    mapping.SourceType,
                    mapping.TargetType,
                    cancellationToken));
            var imageGroups = await Task.WhenAll(requests).ConfigureAwait(false);

            var extendedImages = imageGroups
                .SelectMany(group => group)
                .Where(image => image != null && !string.IsNullOrWhiteSpace(image.Url))
                .GroupBy(
                    image => $"{image.Type}:{image.Url}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .GroupBy(image => image.Type)
                .SelectMany(group => group.Take(GetImageLimit(group.Key)));

            var result = baseImages
                .Concat(extendedImages)
                .Where(image => image != null && !string.IsNullOrWhiteSpace(image.Url))
                .GroupBy(
                    image => $"{image.Type}:{image.Url}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();

            _logger.LogDebug(
                "Для объекта КиноПоиска {KinopoiskId} подготовлено {ImageCount} удалённых изображений",
                kinopoiskId,
                result.Length);

            return result;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetImagesSafely(
            int kinopoiskId,
            FilmImageType sourceType,
            ImageType targetType,
            CancellationToken cancellationToken)
        {
            try
            {
                var response = await _imageApiClient
                    .GetImages(kinopoiskId, sourceType, 1, cancellationToken)
                    .ConfigureAwait(false);

                if (response?.Items is null)
                    return Enumerable.Empty<RemoteImageInfo>();

                return response.Items
                    .Where(image => image != null && !string.IsNullOrWhiteSpace(image.ImageUrl))
                    .Select(image => new RemoteImageInfo
                    {
                        Type = targetType,
                        Url = image.ImageUrl,
                        Language = Constants.ProviderMetadataLanguage,
                        ProviderName = Constants.ProviderName
                    })
                    .ToArray();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Не удалось получить изображения типа {ImageType} для объекта КиноПоиска {KinopoiskId}",
                    sourceType,
                    kinopoiskId);
                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        private static int GetImageLimit(ImageType imageType)
        {
            return imageType switch
            {
                ImageType.Primary => 30,
                ImageType.Backdrop => 60,
                ImageType.Screenshot => 60,
                _ => 20
            };
        }
    }
}
