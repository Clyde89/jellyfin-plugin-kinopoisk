using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class VideoImageProvider : BaseImageProvider
    {
        private const int MaximumApiPages = 20;

        private static readonly (FilmImageType SourceType, ImageType TargetType)[] ImageMappings =
        {
            (FilmImageType.POSTER, ImageType.Primary),
            (FilmImageType.COVER, ImageType.Backdrop),
            (FilmImageType.FAN_ART, ImageType.Backdrop),
            (FilmImageType.WALLPAPER, ImageType.Backdrop),
            (FilmImageType.PROMO, ImageType.Backdrop),
            (FilmImageType.CONCEPT, ImageType.Backdrop),
            (FilmImageType.STILL, ImageType.Backdrop),
            (FilmImageType.SCREENSHOT, ImageType.Thumb),
            (FilmImageType.SHOOTING, ImageType.Thumb)
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
            : this(
                kinopoiskApiClient,
                providerIdResolver,
                logger,
                httpClientFactory,
                null)
        {
        }

        [ActivatorUtilitiesConstructor]
        public VideoImageProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IProviderIdResolver<BaseItem> providerIdResolver,
            ILogger<VideoImageProvider> logger,
            IHttpClientFactory httpClientFactory,
            KinopoiskImageBinaryCache imageBinaryCache)
            : base(httpClientFactory, imageBinaryCache)
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
                    ImageType.Thumb,
                    ImageType.Logo
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
            var baseImages = await FilterEmptyImages(CreateBaseImages(film))
                .ConfigureAwait(false);

            if (_imageApiClient is null)
                return LimitByType(baseImages);

            var requests = ImageMappings.Select(mapping =>
                GetImagesSafely(
                    kinopoiskId,
                    mapping.SourceType,
                    mapping.TargetType,
                    cancellationToken));
            var imageGroups = await Task.WhenAll(requests).ConfigureAwait(false);

            var allCandidates = baseImages
                .Concat(imageGroups.SelectMany(group => group))
                .Where(image => image is not null && !string.IsNullOrWhiteSpace(image.Url))
                .GroupBy(
                    image => $"{image.Type}:{image.Url}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToArray();
            var result = LimitByType(allCandidates).ToArray();

            _logger.LogDebug(
                "Для объекта КиноПоиска {KinopoiskId} подготовлено {ImageCount} удалённых изображений",
                kinopoiskId,
                result.Length);

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Debug,
                typeof(VideoImageProvider).FullName ?? nameof(VideoImageProvider),
                "images.selection.completed",
                "Завершён отбор удалённых изображений КиноПоиска.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["kinopoiskId"] = kinopoiskId.ToString(CultureInfo.InvariantCulture),
                    ["selected"] = result.Length.ToString(CultureInfo.InvariantCulture),
                    ["primary"] = result.Count(image => image.Type == ImageType.Primary)
                        .ToString(CultureInfo.InvariantCulture),
                    ["backdrop"] = result.Count(image => image.Type == ImageType.Backdrop)
                        .ToString(CultureInfo.InvariantCulture),
                    ["thumb"] = result.Count(image => image.Type == ImageType.Thumb)
                        .ToString(CultureInfo.InvariantCulture),
                    ["logo"] = result.Count(image => image.Type == ImageType.Logo)
                        .ToString(CultureInfo.InvariantCulture)
                });

            return result;
        }

        private async Task<IEnumerable<RemoteImageInfo>> GetImagesSafely(
            int kinopoiskId,
            FilmImageType sourceType,
            ImageType targetType,
            CancellationToken cancellationToken)
        {
            var received = new List<RemoteImageInfo>();
            var pagesRequested = 0;

            try
            {
                var maximumImages = GetImageLimit(targetType);
                var totalPages = 1;

                for (var page = 1;
                    page <= Math.Min(totalPages, MaximumApiPages)
                        && received.Count < maximumImages;
                    page++)
                {
                    var response = await _imageApiClient
                        .GetImages(kinopoiskId, sourceType, page, cancellationToken)
                        .ConfigureAwait(false);
                    pagesRequested++;

                    if (page == 1)
                        totalPages = Math.Max(1, GetTotalPages(response));

                    var pageItems = response?.Items?
                        .Where(image => image is not null && IsHttpUrl(image.ImageUrl))
                        .Select(image => new RemoteImageInfo
                        {
                            Type = targetType,
                            Url = image.ImageUrl,
                            Language = Constants.ProviderMetadataLanguage,
                            ProviderName = Constants.ProviderName
                        })
                        .ToArray()
                        ?? Array.Empty<RemoteImageInfo>();

                    if (pageItems.Length == 0)
                        break;

                    received.AddRange(pageItems);
                }

                var selected = received
                    .GroupBy(image => image.Url, StringComparer.OrdinalIgnoreCase)
                    .Select(group => group.First())
                    .Take(maximumImages)
                    .ToArray();

                RecordImageSourceDiagnostics(
                    kinopoiskId,
                    sourceType,
                    targetType,
                    pagesRequested,
                    received.Count,
                    selected.Length,
                    null);

                return selected;
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

                RecordImageSourceDiagnostics(
                    kinopoiskId,
                    sourceType,
                    targetType,
                    pagesRequested,
                    received.Count,
                    0,
                    exception.GetType().Name);

                return Enumerable.Empty<RemoteImageInfo>();
            }
        }

        private static IEnumerable<RemoteImageInfo> CreateBaseImages(Film film)
        {
            if (film is null)
                return Enumerable.Empty<RemoteImageInfo>();

            var result = new List<RemoteImageInfo>();
            AddBaseImage(result, film.PosterUrl, ImageType.Primary);
            AddBaseImage(result, film.CoverUrl, ImageType.Backdrop);
            AddBaseImage(result, film.LogoUrl, ImageType.Logo);
            return result;
        }

        private static void AddBaseImage(
            ICollection<RemoteImageInfo> images,
            string url,
            ImageType imageType)
        {
            if (!IsHttpUrl(url))
                return;

            images.Add(new RemoteImageInfo
            {
                Type = imageType,
                Url = url,
                Language = Constants.ProviderMetadataLanguage,
                ProviderName = Constants.ProviderName
            });
        }

        private static IEnumerable<RemoteImageInfo> LimitByType(
            IEnumerable<RemoteImageInfo> images)
        {
            return images
                .Where(image => image is not null && !string.IsNullOrWhiteSpace(image.Url))
                .GroupBy(
                    image => $"{image.Type}:{image.Url}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .GroupBy(image => image.Type)
                .SelectMany(group => group.Take(GetImageLimit(group.Key)));
        }

        private static int GetTotalPages(ImageResponse response)
            => response?.TotalPages > 0 ? response.TotalPages : 1;

        private static bool IsHttpUrl(string value)
        {
            return Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
        }

        private static void RecordImageSourceDiagnostics(
            int kinopoiskId,
            FilmImageType sourceType,
            ImageType targetType,
            int pagesRequested,
            int received,
            int selected,
            string errorType)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["kinopoiskId"] = kinopoiskId.ToString(CultureInfo.InvariantCulture),
                ["sourceType"] = sourceType.ToString(),
                ["targetType"] = targetType.ToString(),
                ["pagesRequested"] = pagesRequested.ToString(CultureInfo.InvariantCulture),
                ["received"] = received.ToString(CultureInfo.InvariantCulture),
                ["selected"] = selected.ToString(CultureInfo.InvariantCulture)
            };

            if (!string.IsNullOrWhiteSpace(errorType))
                fields["errorType"] = errorType;

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                string.IsNullOrWhiteSpace(errorType) ? LogLevel.Trace : LogLevel.Warning,
                typeof(VideoImageProvider).FullName ?? nameof(VideoImageProvider),
                string.IsNullOrWhiteSpace(errorType)
                    ? "images.source.completed"
                    : "images.source.failed",
                string.IsNullOrWhiteSpace(errorType)
                    ? "Завершена загрузка типа изображений КиноПоиска."
                    : "Загрузка типа изображений КиноПоиска завершилась ошибкой.",
                fields);
        }

        private static int GetImageLimit(ImageType imageType)
        {
            return imageType switch
            {
                ImageType.Primary => 30,
                ImageType.Backdrop => 60,
                ImageType.Thumb => 60,
                ImageType.Logo => 5,
                _ => 20
            };
        }
    }
}
