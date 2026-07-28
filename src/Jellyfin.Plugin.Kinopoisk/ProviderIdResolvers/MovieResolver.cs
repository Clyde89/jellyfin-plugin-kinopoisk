using System;
using System.Collections.Generic;
using System.Linq;
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

            if (string.IsNullOrWhiteSpace(info.Name))
            {
                _logger.LogDebug("Название отсутствует, поиск идентификатора КиноПоиска пропущен");
                return (false, 0);
            }

            _logger.LogDebug("Выполнен поиск кандидатов КиноПоиска для '{Name}'", info.Name);
            var searchResult = await _kinopoiskApiClient.SearchByKeyword(info.Name, 1, ct ?? CancellationToken.None);
            if (searchResult.SearchFilmsCountResult < 1 || searchResult?.Films.Count < 1)
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

            possibleResult = await TryResolveBySingleCandidateLeft(info, candidatesByYear, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            possibleResult = await TryResolveByImdbMatch(info, candidatesByYear, ct);
            if (possibleResult.IsSuccess)
                return possibleResult;

            possibleResult = await TryResolveByImdbMatch(info, candidatesByType, ct);
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

            var targetYear = info.Year.Value.ToString();
            var result = candidates.Where(candidate => candidate.Year == targetYear).ToArray();
            _logger.LogDebug("После фильтрации по году {Year} осталось кандидатов: {Count}", targetYear, result.Length);
            return result;
        }
    }
}
