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

            _logger.LogDebug("Выполнен поиск кандидатов КиноПоиска для '{Name}'", searchTitle);
            var searchResult = await _kinopoiskApiClient.SearchByKeyword(searchTitle, 1, ct ?? CancellationToken.None);
            if (searchResult?.Films == null || searchResult.SearchFilmsCountResult < 1 || searchResult.Films.Count < 1)
            {
                _logger.LogDebug("Поиск КиноПоиска не вернул кандидатов");
                return (false, 0);
            }

            var candidates = searchResult.Films.ToArray();
            _logger.LogDebug("Получено кандидатов: {Count}", candidates.Length);

            var candidatesByType = FilterByContentType(candidates);
            if (candidatesByType.Count < 1)
            {
                _logger.LogDebug("Совместимые кандидаты по типу контента не найдены ({Name})", info.Name);
                return (false, 0);
            }

            var candidatesByYear = FilterByYear(info, candidatesByType);

            possibleResult = await TryResolveByImdbMatch(info, candidatesByYear, ct);
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
