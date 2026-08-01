using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public abstract class BaseVideoMetadataProvider<TItemType, TLookupInfoType> : BaseMetadataProvider, IRemoteMetadataProvider<TItemType, TLookupInfoType>
        where TItemType : BaseItem, IHasLookupInfo<TLookupInfoType>
        where TLookupInfoType : ItemLookupInfo, new()
    {
        private readonly ILogger _logger;
        private readonly IKinopoiskApiClient _apiClient;
        private readonly IKinopoiskDistributionApiClient _distributionApiClient;
        private readonly IProviderIdResolver<TLookupInfoType> _providerIdResolver;

        protected BaseVideoMetadataProvider(
            IKinopoiskApiClient kinopoiskApiClient,
            IKinopoiskDistributionApiClient distributionApiClient,
            IProviderIdResolver<TLookupInfoType> providerIdResolver,
            ILogger logger,
            IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _apiClient = kinopoiskApiClient ?? throw new ArgumentNullException(nameof(kinopoiskApiClient));
            _distributionApiClient = distributionApiClient
                ?? throw new ArgumentNullException(nameof(distributionApiClient));
            _providerIdResolver = providerIdResolver
                ?? throw new ArgumentNullException(nameof(providerIdResolver));
        }

        protected abstract TItemType ConvertResponseToItem(Film apiResponse);

        public async Task<MetadataResult<TItemType>> GetMetadata(
            TLookupInfoType info,
            CancellationToken cancellationToken)
        {
            var correlationId = Guid.NewGuid().ToString("N");
            using var diagnosticScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["KinopoiskCorrelationId"] = correlationId,
                ["KinopoiskOperation"] = "GetMetadata",
                ["KinopoiskItemType"] = typeof(TLookupInfoType).Name,
                ["KinopoiskItemName"] = info?.Name ?? string.Empty,
                ["KinopoiskYear"] = info?.Year?.ToString() ?? string.Empty
            });
            var stopwatch = Stopwatch.StartNew();
            var outcome = "not_completed";
            var resolvedKinopoiskId = 0;

            _logger.LogDebug(
                "Начата обработка метаданных {ItemType} '{Name}', год {Year}, correlation ID {CorrelationId}",
                typeof(TLookupInfoType).Name,
                info?.Name,
                info?.Year,
                correlationId);

            try
            {
                var result = new MetadataResult<TItemType>
                {
                    QueriedById = true,
                    Provider = Constants.ProviderName,
                    ResultLanguage = Constants.ProviderMetadataLanguage
                };

                if (!IsMetadataEnabled())
                {
                    outcome = "disabled";
                    return result;
                }

                var hadStoredKinopoiskId = info.TryGetProviderId(Constants.ProviderId, out _);
                var hadPathKinopoiskId = VideoLookupInfoHelper.TryGetKinopoiskIdFromPath(
                    info,
                    out var pathKinopoiskId);

                var (resolveResult, kinopoiskId) = await _providerIdResolver
                    .TryResolve(info, cancellationToken)
                    .ConfigureAwait(false);
                resolvedKinopoiskId = kinopoiskId;
                if (!resolveResult)
                {
                    outcome = "not_resolved";
                    return result;
                }

                var film = await _apiClient
                    .GetSingleFilm(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);

                cancellationToken.ThrowIfCancellationRequested();

                if (!IsResolvedCardValid(film, kinopoiskId))
                {
                    outcome = "invalid_card";
                    return result;
                }

                var usedPathKinopoiskId = hadPathKinopoiskId && pathKinopoiskId == kinopoiskId;
                if (!hadStoredKinopoiskId
                    && !usedPathKinopoiskId
                    && !IsAutomaticMatchValid(info, film, kinopoiskId))
                {
                    outcome = "automatic_match_rejected";
                    return result;
                }

                info.SetProviderId(Constants.ProviderId, Convert.ToString(kinopoiskId));

                if (hadStoredKinopoiskId)
                {
                    _logger.LogDebug(
                        "Использован сохранённый Kinopoisk ID {KinopoiskId} для '{Name}'",
                        kinopoiskId,
                        info.Name);
                }
                else if (usedPathKinopoiskId)
                {
                    _logger.LogDebug(
                        "Использован Kinopoisk ID {KinopoiskId} из пути для '{Name}'",
                        kinopoiskId,
                        info.Name);
                }
                else
                {
                    _logger.LogInformation(
                        "Автоматически сопоставлен Kinopoisk ID {KinopoiskId} для '{Name}'",
                        kinopoiskId,
                        info.Name);
                }

                result.Item = ConvertResponseToItem(film);
                if (result.Item is null)
                {
                    outcome = "conversion_failed";
                    _logger.LogWarning(
                        "Основные метаданные не преобразованы для Kinopoisk ID {KinopoiskId}",
                        kinopoiskId);
                    return result;
                }

                foreach (var providerId in info.ProviderIds)
                    result.Item.ProviderIds.TryAdd(providerId.Key, providerId.Value);

                result.Item.SetProviderId(Constants.ProviderId, Convert.ToString(kinopoiskId));
                result.HasMetadata = true;

                await AddPrecisePremiereDate(result, kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                await AddStaff(result, kinopoiskId, cancellationToken).ConfigureAwait(false);
                await AddTrailers(result, kinopoiskId, cancellationToken).ConfigureAwait(false);

                outcome = "metadata_ready";
                return result;
            }
            catch (OperationCanceledException)
            {
                outcome = "cancelled";
                throw;
            }
            catch
            {
                outcome = "failed";
                throw;
            }
            finally
            {
                stopwatch.Stop();
                _logger.LogDebug(
                    "Завершена обработка метаданных {ItemType} '{Name}': результат {Outcome}, Kinopoisk ID {KinopoiskId}, длительность {DurationMs} мс, correlation ID {CorrelationId}",
                    typeof(TLookupInfoType).Name,
                    info?.Name,
                    outcome,
                    resolvedKinopoiskId,
                    stopwatch.ElapsedMilliseconds,
                    correlationId);
            }
        }

        public Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            TLookupInfoType searchInfo,
            CancellationToken cancellationToken)
        {
            if (!IsMetadataEnabled())
                return Task.FromResult(Enumerable.Empty<RemoteSearchResult>());

            using var diagnosticScope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["KinopoiskCorrelationId"] = Guid.NewGuid().ToString("N"),
                ["KinopoiskOperation"] = "GetSearchResults",
                ["KinopoiskItemType"] = typeof(TLookupInfoType).Name,
                ["KinopoiskItemName"] = searchInfo?.Name ?? string.Empty,
                ["KinopoiskYear"] = searchInfo?.Year?.ToString() ?? string.Empty
            });

            return VideoRemoteSearchService.Search(
                _apiClient,
                _logger,
                searchInfo,
                cancellationToken);
        }

        protected async Task<IEnumerable<PersonInfo>> SanitizeEmptyImagePersonInfos(
            IEnumerable<PersonInfo> images)
        {
            using var httpClient = new HttpClient(
                new HttpClientHandler { AllowAutoRedirect = false },
                true);
            var sanitizer = new RemoteImageUrlSanitizer(httpClient);
            var result = await Task.WhenAll(images.Select(async person =>
            {
                person.ImageUrl = await sanitizer
                    .SanitizeRemoteImageUrl(person.ImageUrl)
                    .ConfigureAwait(false);
                return person;
            })).ConfigureAwait(false);

            return result.Where(item => item is not null).ToArray();
        }

        private static bool IsMetadataEnabled()
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration is null)
                return true;

            if (typeof(TLookupInfoType) == typeof(MovieInfo))
                return configuration.EnableMovieMetadata;

            if (typeof(TLookupInfoType) == typeof(SeriesInfo))
                return configuration.EnableSeriesMetadata;

            return true;
        }

        private bool IsResolvedCardValid(Film film, int kinopoiskId)
        {
            if (film is null)
            {
                _logger.LogWarning(
                    "Полная карточка не получена для Kinopoisk ID {KinopoiskId}",
                    kinopoiskId);
                return false;
            }

            if (film.KinopoiskId != kinopoiskId)
            {
                _logger.LogWarning(
                    "Полученная карточка содержит Kinopoisk ID {ActualKinopoiskId} вместо ожидаемого {ExpectedKinopoiskId}",
                    film.KinopoiskId,
                    kinopoiskId);
                return false;
            }

            return true;
        }

        private bool IsAutomaticMatchValid(
            TLookupInfoType info,
            Film film,
            int kinopoiskId)
        {
            if (!IsCompatibleFilmType(film.Type))
            {
                _logger.LogWarning(
                    "Автоматическое сопоставление Kinopoisk ID {KinopoiskId} отклонено из-за несовместимого типа {FilmType}",
                    kinopoiskId,
                    film.Type);
                return false;
            }

            if (info.TryGetProviderId(MetadataProvider.Imdb, out var imdbId)
                && !string.IsNullOrWhiteSpace(imdbId))
            {
                if (!string.Equals(
                    imdbId.Trim(),
                    film.ImdbId?.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogWarning(
                        "Автоматическое сопоставление Kinopoisk ID {KinopoiskId} отклонено: IMDb ID полной карточки '{ActualImdbId}' не совпал с '{ExpectedImdbId}'",
                        kinopoiskId,
                        film.ImdbId,
                        imdbId);
                    return false;
                }

                _logger.LogDebug(
                    "Полная карточка Kinopoisk ID {KinopoiskId} подтверждена по IMDb ID '{ImdbId}'",
                    kinopoiskId,
                    imdbId);
                return true;
            }

            if (info.Year.HasValue && film.GetProductionYear() != info.Year.Value)
            {
                _logger.LogWarning(
                    "Автоматическое сопоставление Kinopoisk ID {KinopoiskId} отклонено: год полной карточки {ActualYear} не совпал с {ExpectedYear}",
                    kinopoiskId,
                    film.GetProductionYear(),
                    info.Year.Value);
                return false;
            }

            var searchTitle = VideoLookupInfoHelper.GetSearchTitle(info);
            if (!VideoLookupInfoHelper.IsExactTitle(searchTitle, film.NameRu)
                && !VideoLookupInfoHelper.IsExactTitle(searchTitle, film.NameEn)
                && !VideoLookupInfoHelper.IsExactTitle(searchTitle, film.NameOriginal))
            {
                _logger.LogWarning(
                    "Автоматическое сопоставление Kinopoisk ID {KinopoiskId} отклонено: название полной карточки не совпало с '{ExpectedTitle}'",
                    kinopoiskId,
                    searchTitle);
                return false;
            }

            _logger.LogDebug(
                "Полная карточка Kinopoisk ID {KinopoiskId} подтверждена по названию '{Name}' и году {Year}",
                kinopoiskId,
                searchTitle,
                info.Year);
            return true;
        }

        private static bool IsCompatibleFilmType(FilmType filmType)
        {
            if (typeof(TLookupInfoType) == typeof(MovieInfo))
                return filmType is FilmType.FILM or FilmType.VIDEO;

            if (typeof(TLookupInfoType) == typeof(SeriesInfo))
            {
                return filmType is FilmType.TV_SHOW
                    or FilmType.TV_SERIES
                    or FilmType.MINI_SERIES;
            }

            return false;
        }

        private async Task AddPrecisePremiereDate(
            MetadataResult<TItemType> result,
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (Plugin.Instance?.Configuration.EnablePrecisePremiereDate == false)
                return;

            try
            {
                var distributions = await _distributionApiClient
                    .GetDistributions(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                result.Item.ApplyPrecisePremiereDate(distributions);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Точная дата премьеры не загружена для Kinopoisk ID {KinopoiskId}",
                    kinopoiskId);
            }
        }

        private async Task AddStaff(
            MetadataResult<TItemType> result,
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (Plugin.Instance?.Configuration.EnablePeopleMetadata == false)
                return;

            try
            {
                var staff = await _apiClient
                    .GetStaff(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                if (staff is null)
                    return;

                var sanitizedPersons = await SanitizeEmptyImagePersonInfos(staff.ToPersonInfos())
                    .ConfigureAwait(false);
                var addedCount = 0;
                foreach (var item in sanitizedPersons)
                {
                    result.AddPerson(item);
                    addedCount++;
                }

                _logger.LogDebug(
                    "Для Kinopoisk ID {KinopoiskId} добавлено участников: {PersonCount}",
                    kinopoiskId,
                    addedCount);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Дополнительные данные об участниках не загружены для Kinopoisk ID {KinopoiskId}",
                    kinopoiskId);
            }
        }

        private async Task AddTrailers(
            MetadataResult<TItemType> result,
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            var configuration = Plugin.Instance?.Configuration;
            if (configuration?.EnableTrailers == false)
                return;

            try
            {
                var trailers = await _apiClient
                    .GetTrailers(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();

                var remoteTrailers = KinopoiskTrailerSelector.Select(
                    trailers,
                    new KinopoiskTrailerSelectionOptions
                    {
                        MaximumTrailers = configuration?.MaximumTrailers ?? 5,
                        PreferOfficialTrailers = configuration?.PreferOfficialTrailers ?? true,
                        PreferRussianTrailers = configuration?.PreferRussianTrailers ?? true,
                        IncludeTeasers = configuration?.IncludeTrailerTeasers ?? true,
                        IncludeAdditionalVideos = configuration?.IncludeAdditionalTrailerVideos ?? false,
                        PrefixTrailerNames = configuration?.PrefixTrailerNames ?? true
                    });

                if (remoteTrailers.Count > 0)
                    result.Item.RemoteTrailers = remoteTrailers;

                _logger.LogDebug(
                    "Для Kinopoisk ID {KinopoiskId} отобрано поддерживаемых трейлеров: {TrailerCount}",
                    kinopoiskId,
                    remoteTrailers.Count);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Дополнительные данные о трейлерах не загружены для Kinopoisk ID {KinopoiskId}",
                    kinopoiskId);
            }
        }
    }
}
