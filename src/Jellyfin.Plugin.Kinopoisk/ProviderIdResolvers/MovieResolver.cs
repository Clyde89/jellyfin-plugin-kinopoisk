using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers
{
    public class VideoResolver<T> : CommonLookupInfoResolver<T>
        where T : ItemLookupInfo
    {
        private static readonly Regex ProviderTagRegex = new(
            @"\s*[\[\{](?:tmdbid|tmdb|imdbid|imdb|tvdbid|tvdb|kp|kinopoiskid|kinopoisk)[-_:\s]?[^\]\}]+[\]\}]\s*$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

        private readonly IKinopoiskApiClient _kinopoiskApiClient;

        public VideoResolver(IKinopoiskApiClient kinopoiskApiClient, ILogger<VideoResolver<T>> logger) : base(logger)
        {
            _kinopoiskApiClient = kinopoiskApiClient ?? throw new ArgumentNullException(nameof(kinopoiskApiClient));
        }

        public override async Task<(bool IsSuccess, int ProviderId)> TryResolve(T info, CancellationToken? ct = null)
        {
            var possibleResult = await base.TryResolve(info, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            var searchTitle = GetSearchTitle(info);

            if (_kinopoiskApiClient is IFilteredKinopoiskApiClient filteredApiClient)
            {
                possibleResult = await TryResolveByFilteredImdb(info, filteredApiClient, ct);
                if (possibleResult.IsSuccess)
                    return possibleResult;

                possibleResult = await TryResolveByFilteredTitle(info, searchTitle, filteredApiClient, ct);
                if (possibleResult.IsSuccess)
                    return possibleResult;
            }

            if (string.IsNullOrWhiteSpace(searchTitle))
            {
                _logger.LogDebug("Название отсутствует, поиск идентификатора КиноПоиска пропущен");
                return (false, 0);
            }

            if (!string.Equals(searchTitle, info.Name?.Trim(), StringComparison.Ordinal))
            {
                _logger.LogDebug(
                    "Название для поиска очищено: '{OriginalName}' -> '{SearchTitle}'",
                    info.Name,
                    searchTitle);
            }

            return await TryResolveByKeywordFallback(info, searchTitle, ct);
        }

        public async Task<(bool IsSuccess, int ProviderId)> TryResolveByImdbMatch(T info, ICollection<FilmSearchResponse_films> candidates, CancellationToken? ct = null)
        {
            if (info.TryGetProviderId(MetadataProvider.Imdb, out var imdbId))
            {
                _logger.LogDebug("Выполнена проверка кандидатов по IMDb ID '{ImdbId}'", imdbId);
                var index = 0;
                foreach (var candidate in candidates)
                {
                    try
                    {
                        var film = await _kinopoiskApiClient.GetSingleFilm(candidate.FilmId, ct);

                        if (imdbId == film?.ImdbId)
                        {
                            _logger.LogDebug("Найдено совпадение: Kinopoisk ID {KinopoiskId}, IMDb ID '{ImdbId}'", candidate.FilmId, film?.ImdbId);
                            return (true, candidate.FilmId);
                        }

                        _logger.LogDebug("Кандидат {KinopoiskId} отклонён по IMDb ID, осталось: {Count}", candidate.FilmId, candidates.Count - ++index);
                    }
                    catch (Exception e)
                    {
                        _logger.LogError(e, "Не удалось получить фильм {KinopoiskId}", candidate.FilmId);
                        continue;
                    }
                }
            }

            return (false, 0);
        }

        public Task<(bool IsSuccess, int ProviderId)> TryResolveByExactTitle(T info, ICollection<FilmSearchResponse_films> candidates, CancellationToken? ct = null)
        {
            return TryResolveByExactTitle(info, GetSearchTitle(info), candidates, ct);
        }

        public Task<(bool IsSuccess, int ProviderId)> TryResolveBySingleCandidateLeft(T info, ICollection<FilmSearchResponse_films> candidates, CancellationToken? ct = null)
        {
            if (candidates.Count == 1)
            {
                var kinopoiskId = candidates.Single().FilmId;
                _logger.LogDebug("Выбран единственный кандидат {KinopoiskId} ({Name})", kinopoiskId, info.Name);
                return Task.FromResult((true, kinopoiskId));
            }

            return Task.FromResult((false, 0));
        }

        public ICollection<FilmSearchResponse_films> FilterByContentType(ICollection<FilmSearchResponse_films> candidates)
        {
            FilmSearchResponse_filmsType expectedType;

            if (typeof(T) == typeof(MovieInfo))
            {
                expectedType = FilmSearchResponse_filmsType.FILM;
            }
            else if (typeof(T) == typeof(SeriesInfo))
            {
                expectedType = FilmSearchResponse_filmsType.TV_SHOW;
            }
            else
            {
                _logger.LogDebug("Тип объекта {ItemType} не поддерживает автоматическое сопоставление", typeof(T).Name);
                return Array.Empty<FilmSearchResponse_films>();
            }

            var result = candidates.Where(candidate => candidate.Type == expectedType).ToArray();
            _logger.LogDebug("После фильтрации по типу {ExpectedType} осталось кандидатов: {Count}", expectedType, result.Length);
            return result;
        }

        public ICollection<FilmSearchResponse_films> FilterByYear(T info, ICollection<FilmSearchResponse_films> candidates)
        {
            if (!info.Year.HasValue)
            {
                _logger.LogDebug("Год отсутствует, фильтрация по году пропущена");
                return Array.Empty<FilmSearchResponse_films>();
            }

            var targetYear = info.Year.Value.ToString(CultureInfo.InvariantCulture);
            var result = candidates.Where(candidate => candidate.Year == targetYear).ToArray();
            _logger.LogDebug("После фильтрации по году {Year} осталось кандидатов: {Count}", targetYear, result.Length);
            return result;
        }

        private async Task<(bool IsSuccess, int ProviderId)> TryResolveByFilteredImdb(
            T info,
            IFilteredKinopoiskApiClient apiClient,
            CancellationToken? ct)
        {
            if (!info.TryGetProviderId(MetadataProvider.Imdb, out var imdbId)
                || string.IsNullOrWhiteSpace(imdbId))
            {
                return (false, 0);
            }

            var queryType = GetFilteredQueryType();
            if (queryType is null)
                return (false, 0);

            _logger.LogDebug(
                "Выполнен фильтрованный поиск КиноПоиска по IMDb ID '{ImdbId}' и типу {ContentType}",
                imdbId,
                queryType);

            var response = await SearchFilmsSafely(
                apiClient,
                new FilmSearchQuery
                {
                    ImdbId = imdbId.Trim(),
                    Type = queryType,
                    Page = 1
                },
                ct);

            var matches = DistinctFilteredCandidates(response)
                .Where(IsCompatibleFilteredType)
                .Where(candidate => string.Equals(
                    candidate.ImdbId?.Trim(),
                    imdbId.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            _logger.LogDebug(
                "После фильтрованного поиска по IMDb осталось кандидатов: {Count}",
                matches.Length);

            if (matches.Length == 1)
            {
                _logger.LogDebug(
                    "Выбран кандидат {KinopoiskId} по точному IMDb ID '{ImdbId}'",
                    matches[0].KinopoiskId,
                    imdbId);
                return (true, matches[0].KinopoiskId);
            }

            if (matches.Length > 1)
            {
                _logger.LogWarning(
                    "Найдено несколько кандидатов КиноПоиска с IMDb ID '{ImdbId}', автоматическое сопоставление отклонено",
                    imdbId);
            }

            return (false, 0);
        }

        private async Task<(bool IsSuccess, int ProviderId)> TryResolveByFilteredTitle(
            T info,
            string searchTitle,
            IFilteredKinopoiskApiClient apiClient,
            CancellationToken? ct)
        {
            if (string.IsNullOrWhiteSpace(searchTitle))
                return (false, 0);

            var queryType = GetFilteredQueryType();
            if (queryType is null)
                return (false, 0);

            _logger.LogDebug(
                "Выполнен фильтрованный поиск КиноПоиска для '{Name}', год {Year}, тип {ContentType}",
                searchTitle,
                info.Year,
                queryType);

            var response = await SearchFilmsSafely(
                apiClient,
                new FilmSearchQuery
                {
                    Keyword = searchTitle,
                    YearFrom = info.Year,
                    YearTo = info.Year,
                    Type = queryType,
                    Page = 1
                },
                ct);

            var candidates = DistinctFilteredCandidates(response)
                .Where(IsCompatibleFilteredType);

            if (info.Year.HasValue)
                candidates = candidates.Where(candidate => candidate.Year == info.Year.Value);

            var exactMatches = candidates
                .Where(candidate =>
                    IsExactTitle(searchTitle, candidate.NameRu)
                    || IsExactTitle(searchTitle, candidate.NameEn)
                    || IsExactTitle(searchTitle, candidate.NameOriginal))
                .ToArray();

            _logger.LogDebug(
                "После фильтрованного сравнения названия, года и типа осталось кандидатов: {Count}",
                exactMatches.Length);

            if (exactMatches.Length == 1)
            {
                _logger.LogDebug(
                    "Выбран кандидат {KinopoiskId} по названию '{Name}' и году {Year}",
                    exactMatches[0].KinopoiskId,
                    searchTitle,
                    info.Year);
                return (true, exactMatches[0].KinopoiskId);
            }

            if (exactMatches.Length > 1)
            {
                _logger.LogWarning(
                    "Найдено несколько точных кандидатов КиноПоиска для '{Name}', автоматическое сопоставление отклонено",
                    searchTitle);
            }

            return (false, 0);
        }

        private async Task<FilteredFilmSearchResponse> SearchFilmsSafely(
            IFilteredKinopoiskApiClient apiClient,
            FilmSearchQuery query,
            CancellationToken? ct)
        {
            try
            {
                return await apiClient.SearchFilms(query, ct ?? CancellationToken.None);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Фильтрованный поиск КиноПоиска не выполнен, использован резервный поиск");
                return null;
            }
        }

        private async Task<(bool IsSuccess, int ProviderId)> TryResolveByKeywordFallback(
            T info,
            string searchTitle,
            CancellationToken? ct)
        {
            _logger.LogDebug("Выполнен резервный поиск кандидатов КиноПоиска для '{Name}'", searchTitle);
            var searchResult = await _kinopoiskApiClient.SearchByKeyword(searchTitle, 1, ct ?? CancellationToken.None);
            if (searchResult?.Films == null || searchResult.SearchFilmsCountResult < 1 || searchResult.Films.Count < 1)
            {
                _logger.LogDebug("Резервный поиск КиноПоиска не вернул кандидатов");
                return (false, 0);
            }

            var candidates = searchResult.Films.ToArray();
            _logger.LogDebug("Получено кандидатов резервного поиска: {Count}", candidates.Length);

            var candidatesByType = FilterByContentType(candidates);
            if (candidatesByType.Count < 1)
            {
                _logger.LogDebug("Совместимые кандидаты по типу контента не найдены ({Name})", info.Name);
                return (false, 0);
            }

            var candidatesByYear = FilterByYear(info, candidatesByType);

            var possibleResult = await TryResolveByImdbMatch(info, candidatesByYear, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            possibleResult = await TryResolveByImdbMatch(info, candidatesByType, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            var candidatesForTitle = info.Year.HasValue
                ? candidatesByYear
                : candidatesByType;

            possibleResult = await TryResolveByExactTitle(info, searchTitle, candidatesForTitle, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            possibleResult = await TryResolveBySingleCandidateLeft(info, candidatesByYear, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            _logger.LogDebug("Однозначное совпадение не найдено, автоматическое сопоставление отклонено ({Name})", info.Name);
            return (false, 0);
        }

        private Task<(bool IsSuccess, int ProviderId)> TryResolveByExactTitle(
            T info,
            string targetTitle,
            ICollection<FilmSearchResponse_films> candidates,
            CancellationToken? ct = null)
        {
            if (string.IsNullOrWhiteSpace(targetTitle))
                return Task.FromResult((false, 0));

            var exactMatches = candidates
                .Where(candidate =>
                    IsExactTitle(targetTitle, candidate.NameRu)
                    || IsExactTitle(targetTitle, candidate.NameEn))
                .ToArray();

            _logger.LogDebug("После точного сравнения названия осталось кандидатов: {Count}", exactMatches.Length);
            return TryResolveBySingleCandidateLeft(info, exactMatches, ct);
        }

        private string GetFilteredQueryType()
        {
            if (typeof(T) == typeof(MovieInfo))
                return "FILM";

            if (typeof(T) == typeof(SeriesInfo))
                return "ALL";

            _logger.LogDebug(
                "Тип объекта {ItemType} не поддерживает фильтрованный поиск",
                typeof(T).Name);
            return null;
        }

        private bool IsCompatibleFilteredType(FilteredFilmSearchItem candidate)
        {
            if (candidate is null || string.IsNullOrWhiteSpace(candidate.Type))
                return false;

            if (typeof(T) == typeof(MovieInfo))
            {
                return string.Equals(candidate.Type, "FILM", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Type, "VIDEO", StringComparison.OrdinalIgnoreCase);
            }

            if (typeof(T) == typeof(SeriesInfo))
            {
                return string.Equals(candidate.Type, "TV_SHOW", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Type, "TV_SERIES", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(candidate.Type, "MINI_SERIES", StringComparison.OrdinalIgnoreCase);
            }

            return false;
        }

        private static IEnumerable<FilteredFilmSearchItem> DistinctFilteredCandidates(
            FilteredFilmSearchResponse response)
        {
            return response?.Items?
                .Where(candidate => candidate is not null && candidate.KinopoiskId > 0)
                .GroupBy(candidate => candidate.KinopoiskId)
                .Select(group => group.First())
                ?? Enumerable.Empty<FilteredFilmSearchItem>();
        }

        private static string GetSearchTitle(T info)
        {
            var title = info.Name?.Trim();
            if (string.IsNullOrWhiteSpace(title))
                return title;

            title = RemoveTrailingProviderTags(title);

            if (info.Year.HasValue)
            {
                var year = Regex.Escape(info.Year.Value.ToString(CultureInfo.InvariantCulture));
                title = Regex.Replace(
                    title,
                    $@"\s*[\(\[]\s*{year}\s*[\)\]]\s*$",
                    string.Empty,
                    RegexOptions.CultureInvariant).Trim();
            }

            return RemoveTrailingProviderTags(title);
        }

        private static string RemoveTrailingProviderTags(string title)
        {
            var result = title;

            while (true)
            {
                var cleaned = ProviderTagRegex.Replace(result, string.Empty).Trim();
                if (string.Equals(cleaned, result, StringComparison.Ordinal))
                    return result;

                result = cleaned;
            }
        }

        private static bool IsExactTitle(string targetTitle, string candidateTitle)
        {
            return !string.IsNullOrWhiteSpace(candidateTitle)
                && string.Equals(targetTitle, candidateTitle.Trim(), StringComparison.OrdinalIgnoreCase);
        }
    }
}
