using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public static class VideoRemoteSearchService
    {
        public static async Task<IEnumerable<RemoteSearchResult>> Search<TLookupInfo>(
            IKinopoiskApiClient apiClient,
            ILogger logger,
            TLookupInfo searchInfo,
            CancellationToken cancellationToken)
            where TLookupInfo : ItemLookupInfo
        {
            if (apiClient is null)
                throw new ArgumentNullException(nameof(apiClient));

            if (logger is null)
                throw new ArgumentNullException(nameof(logger));

            if (searchInfo is null)
                throw new ArgumentNullException(nameof(searchInfo));

            if (searchInfo.TryGetProviderId(Constants.ProviderId, out var kinopoiskIdText)
                && int.TryParse(kinopoiskIdText, out var kinopoiskId))
            {
                var film = await apiClient.GetSingleFilm(kinopoiskId, cancellationToken);
                var result = film.ToRemoteSearchResult();
                return result is null
                    ? Enumerable.Empty<RemoteSearchResult>()
                    : Enumerable.Repeat(result, 1);
            }

            var searchTitle = VideoLookupInfoHelper.GetSearchTitle(searchInfo);

            if (apiClient is IFilteredKinopoiskApiClient filteredApiClient)
            {
                var filteredResults = await SearchFiltered(
                    filteredApiClient,
                    logger,
                    searchInfo,
                    searchTitle,
                    cancellationToken);

                if (filteredResults.Count > 0)
                    return filteredResults;
            }

            if (string.IsNullOrWhiteSpace(searchTitle))
                return Enumerable.Empty<RemoteSearchResult>();

            logger.LogDebug(
                "Выполнен резервный ручной поиск КиноПоиска для '{Name}'",
                searchTitle);

            return (await apiClient.SearchByKeyword(
                searchTitle,
                cancellationToken: cancellationToken)).ToRemoteSearchResults(logger);
        }

        private static async Task<IReadOnlyList<RemoteSearchResult>> SearchFiltered<TLookupInfo>(
            IFilteredKinopoiskApiClient apiClient,
            ILogger logger,
            TLookupInfo searchInfo,
            string searchTitle,
            CancellationToken cancellationToken)
            where TLookupInfo : ItemLookupInfo
        {
            try
            {
                if (searchInfo.TryGetProviderId(MetadataProvider.Imdb, out var imdbId)
                    && !string.IsNullOrWhiteSpace(imdbId))
                {
                    logger.LogDebug(
                        "Выполнен фильтрованный ручной поиск КиноПоиска по IMDb ID '{ImdbId}'",
                        imdbId);

                    var response = await apiClient.SearchFilms(
                        new FilmSearchQuery
                        {
                            ImdbId = imdbId.Trim(),
                            Type = GetQueryType<TLookupInfo>(),
                            Page = 1
                        },
                        cancellationToken);

                    var imdbMatches = GetDistinctCandidates(response)
                        .Where(candidate => IsCompatibleType<TLookupInfo>(candidate.Type))
                        .Where(candidate => string.Equals(
                            candidate.ImdbId?.Trim(),
                            imdbId.Trim(),
                            StringComparison.OrdinalIgnoreCase))
                        .Select(ToRemoteSearchResult)
                        .Where(result => result is not null)
                        .ToArray();

                    if (imdbMatches.Length > 0)
                        return imdbMatches;
                }

                if (string.IsNullOrWhiteSpace(searchTitle))
                    return Array.Empty<RemoteSearchResult>();

                logger.LogDebug(
                    "Выполнен фильтрованный ручной поиск КиноПоиска для '{Name}', год {Year}",
                    searchTitle,
                    searchInfo.Year);

                var titleResponse = await apiClient.SearchFilms(
                    new FilmSearchQuery
                    {
                        Keyword = searchTitle,
                        YearFrom = searchInfo.Year,
                        YearTo = searchInfo.Year,
                        Type = GetQueryType<TLookupInfo>(),
                        Page = 1
                    },
                    cancellationToken);

                var candidates = GetDistinctCandidates(titleResponse)
                    .Where(candidate => IsCompatibleType<TLookupInfo>(candidate.Type));

                if (searchInfo.Year.HasValue)
                    candidates = candidates.Where(candidate => candidate.Year == searchInfo.Year.Value);

                return candidates
                    .Where(candidate =>
                        VideoLookupInfoHelper.IsExactTitle(searchTitle, candidate.NameRu)
                        || VideoLookupInfoHelper.IsExactTitle(searchTitle, candidate.NameEn)
                        || VideoLookupInfoHelper.IsExactTitle(searchTitle, candidate.NameOriginal))
                    .Select(ToRemoteSearchResult)
                    .Where(result => result is not null)
                    .ToArray();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogWarning(
                    exception,
                    "Фильтрованный ручной поиск КиноПоиска не выполнен, использован резервный поиск");
                return Array.Empty<RemoteSearchResult>();
            }
        }

        private static IEnumerable<FilteredFilmSearchItem> GetDistinctCandidates(
            FilteredFilmSearchResponse response)
        {
            return response?.Items?
                .Where(candidate => candidate is not null && candidate.KinopoiskId > 0)
                .GroupBy(candidate => candidate.KinopoiskId)
                .Select(group => group.First())
                ?? Enumerable.Empty<FilteredFilmSearchItem>();
        }

        private static string GetQueryType<TLookupInfo>()
            where TLookupInfo : ItemLookupInfo
        {
            return typeof(TLookupInfo) == typeof(MovieInfo)
                ? "FILM"
                : "ALL";
        }

        private static bool IsCompatibleType<TLookupInfo>(string candidateType)
            where TLookupInfo : ItemLookupInfo
        {
            if (string.IsNullOrWhiteSpace(candidateType))
                return false;

            if (typeof(TLookupInfo) == typeof(MovieInfo))
            {
                return string.Equals(candidateType, "FILM", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidateType, "VIDEO", StringComparison.OrdinalIgnoreCase);
            }

            if (typeof(TLookupInfo) == typeof(SeriesInfo))
            {
                return string.Equals(candidateType, "TV_SHOW", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidateType, "TV_SERIES", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidateType, "MINI_SERIES", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static RemoteSearchResult ToRemoteSearchResult(
            FilteredFilmSearchItem candidate)
        {
            if (candidate is null || candidate.KinopoiskId < 1)
                return null;

            var name = candidate.NameRu;
            if (string.IsNullOrWhiteSpace(name))
                name = candidate.NameOriginal;
            if (string.IsNullOrWhiteSpace(name))
                name = candidate.NameEn;

            var result = new RemoteSearchResult
            {
                Name = name,
                ImageUrl = candidate.PosterUrlPreview ?? candidate.PosterUrl,
                ProductionYear = candidate.Year,
                PremiereDate = candidate.Year is > 1900
                    ? new DateTime(candidate.Year.Value, 1, 1)
                    : null,
                SearchProviderName = Constants.ProviderName
            };

            result.SetProviderId(
                Constants.ProviderId,
                candidate.KinopoiskId.ToString(System.Globalization.CultureInfo.InvariantCulture));

            if (!string.IsNullOrWhiteSpace(candidate.ImdbId))
                result.SetProviderId(MetadataProvider.Imdb, candidate.ImdbId.Trim());

            return result;
        }
    }
}
