#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Presentation
{
    /// <summary>
    /// Формирует безопасную клиентскую модель расширенной карточки.
    /// </summary>
    public sealed class KinopoiskPresentationService
    {
        private static readonly IReadOnlyDictionary<StaffResponseProfessionKey, (int Order, string Label)> ProfessionLabels
            = new Dictionary<StaffResponseProfessionKey, (int Order, string Label)>
            {
                [StaffResponseProfessionKey.DIRECTOR] = (10, "Режиссёры"),
                [StaffResponseProfessionKey.OPERATOR] = (20, "Операторы"),
                [StaffResponseProfessionKey.WRITER] = (30, "Сценаристы"),
                [StaffResponseProfessionKey.PRODUCER] = (40, "Продюсеры"),
                [StaffResponseProfessionKey.COMPOSER] = (50, "Композиторы"),
                [StaffResponseProfessionKey.EDITOR] = (60, "Монтаж"),
                [StaffResponseProfessionKey.DESIGN] = (70, "Художники"),
                [StaffResponseProfessionKey.ACTOR] = (80, "Актёры"),
                [StaffResponseProfessionKey.TRANSLATOR] = (90, "Переводчики"),
                [StaffResponseProfessionKey.VOICE_DIRECTOR] = (100, "Режиссёры дубляжа"),
                [StaffResponseProfessionKey.UNKNOWN] = (1000, "Другие участники")
            };

        private readonly IKinopoiskApiClient _apiClient;
        private readonly IKinopoiskDistributionApiClient _distributionApiClient;
        private readonly IKinopoiskRelationsApiClient _relationsApiClient;
        private readonly ILogger<KinopoiskPresentationService> _logger;

        public KinopoiskPresentationService(
            IKinopoiskApiClient apiClient,
            IKinopoiskDistributionApiClient distributionApiClient,
            IKinopoiskRelationsApiClient relationsApiClient,
            ILogger<KinopoiskPresentationService> logger)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _distributionApiClient = distributionApiClient
                ?? throw new ArgumentNullException(nameof(distributionApiClient));
            _relationsApiClient = relationsApiClient
                ?? throw new ArgumentNullException(nameof(relationsApiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<KinopoiskPresentationResponse> Get(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            if (kinopoiskId < 1)
                throw new ArgumentOutOfRangeException(nameof(kinopoiskId));

            var filmTask = _apiClient.GetSingleFilm(kinopoiskId, cancellationToken);
            var staffTask = GetStaffSafely(kinopoiskId, cancellationToken);
            var distributionsTask = GetDistributionsSafely(kinopoiskId, cancellationToken);
            var relationsTask = GetRelationsSafely(kinopoiskId, cancellationToken);

            var film = await filmTask.ConfigureAwait(false);
            var staff = await staffTask.ConfigureAwait(false);
            var distributions = await distributionsTask.ConfigureAwait(false);
            var relations = await relationsTask.ConfigureAwait(false);

            return new KinopoiskPresentationResponse
            {
                KinopoiskId = film.KinopoiskId > 0 ? film.KinopoiskId : kinopoiskId,
                Name = FirstNotEmpty(film.NameRu, film.NameEn, film.NameOriginal),
                OriginalName = FirstNotEmpty(film.NameOriginal, film.NameEn),
                ImdbId = film.ImdbId ?? string.Empty,
                KinopoiskUrl = NormalizeKinopoiskUrl(film.WebUrl, kinopoiskId),
                ImdbUrl = NormalizeImdbUrl(film.ImdbId),
                Ratings = MapRatings(film),
                ReleaseDates = MapReleaseDates(distributions),
                Professions = MapProfessions(staff),
                Relations = MapRelations(relations)
            };
        }

        private async Task<ICollection<StaffResponse>> GetStaffSafely(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _apiClient.GetStaff(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Участники расширенной карточки Kinopoisk ID {KinopoiskId} не загружены",
                    kinopoiskId);
                return Array.Empty<StaffResponse>();
            }
        }

        private async Task<DistributionResponse?> GetDistributionsSafely(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _distributionApiClient
                    .GetDistributions(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Прокатные даты расширенной карточки Kinopoisk ID {KinopoiskId} не загружены",
                    kinopoiskId);
                return null;
            }
        }

        private async Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelationsSafely(
            int kinopoiskId,
            CancellationToken cancellationToken)
        {
            try
            {
                return await _relationsApiClient
                    .GetRelations(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Связанные фильмы расширенной карточки Kinopoisk ID {KinopoiskId} не загружены",
                    kinopoiskId);
                return Array.Empty<FilmSequelsAndPrequelsResponse>();
            }
        }

        private static KinopoiskRatingInfo MapRatings(Film film)
        {
            return new KinopoiskRatingInfo
            {
                Kinopoisk = PositiveOrNull(film.RatingKinopoisk),
                KinopoiskVotes = Math.Max(0, film.RatingKinopoiskVoteCount),
                Imdb = PositiveOrNull(film.RatingImdb),
                ImdbVotes = Math.Max(0, film.RatingImdbVoteCount),
                RussianCritics = PositiveOrNull(film.RatingRfCritics),
                RussianCriticsVotes = Math.Max(0, film.RatingRfCriticsVoteCount),
                WorldCritics = PositiveOrNull(film.RatingFilmCritics),
                WorldCriticsVotes = Math.Max(0, film.RatingFilmCriticsVoteCount)
            };
        }

        private static IReadOnlyList<KinopoiskReleaseDateInfo> MapReleaseDates(
            DistributionResponse? response)
        {
            return response?.Items?
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Date))
                .Select(item => new KinopoiskReleaseDateInfo
                {
                    Type = item.Type.ToString(),
                    SubType = item.HasSubType ? item.SubType.ToString() : string.Empty,
                    Date = item.Date,
                    Country = item.Country?.Country1 ?? string.Empty,
                    ReRelease = item.ReRelease,
                    Source = "КиноПоиск"
                })
                .GroupBy(
                    item => $"{item.Type}|{item.SubType}|{item.Date}|{item.Country}|{item.ReRelease}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(item => item.Date, StringComparer.Ordinal)
                .ThenBy(item => item.Country, StringComparer.OrdinalIgnoreCase)
                .ToArray()
                ?? Array.Empty<KinopoiskReleaseDateInfo>();
        }

        private static IReadOnlyList<KinopoiskProfessionGroup> MapProfessions(
            ICollection<StaffResponse> staff)
        {
            return (staff ?? Array.Empty<StaffResponse>())
                .Where(item => item is not null)
                .Select(item => new
                {
                    Item = item,
                    Key = item.OriginalProfessionKey != StaffResponseProfessionKey.UNKNOWN
                        ? item.OriginalProfessionKey
                        : item.ProfessionKey
                })
                .Where(item => !string.IsNullOrWhiteSpace(item.Item.NameRu)
                    || !string.IsNullOrWhiteSpace(item.Item.NameEn))
                .GroupBy(item => item.Key)
                .Select(group =>
                {
                    var metadata = ProfessionLabels.TryGetValue(group.Key, out var value)
                        ? value
                        : (
                            Order: 900,
                            Label: NormalizeProfessionLabel(group.First().Item.ProfessionText));
                    var people = group
                        .GroupBy(item => item.Item.StaffId)
                        .Select(personGroup => personGroup.First().Item)
                        .Select(person => new KinopoiskPersonInfo
                        {
                            KinopoiskId = person.StaffId,
                            Name = FirstNotEmpty(person.NameRu, person.NameEn),
                            OriginalName = person.NameEn ?? string.Empty,
                            Profession = person.ProfessionText ?? string.Empty,
                            PosterUrl = person.PosterUrl ?? string.Empty
                        })
                        .OrderBy(person => person.Name, StringComparer.OrdinalIgnoreCase)
                        .ToArray();
                    return new
                    {
                        metadata.Order,
                        Group = new KinopoiskProfessionGroup
                        {
                            Key = group.Key.ToString(),
                            Label = metadata.Label,
                            People = people
                        }
                    };
                })
                .OrderBy(item => item.Order)
                .ThenBy(item => item.Group.Label, StringComparer.OrdinalIgnoreCase)
                .Select(item => item.Group)
                .ToArray();
        }

        private static IReadOnlyList<KinopoiskRelationInfo> MapRelations(
            ICollection<FilmSequelsAndPrequelsResponse> relations)
        {
            return (relations ?? Array.Empty<FilmSequelsAndPrequelsResponse>())
                .Where(item => item is not null && item.FilmId > 0)
                .GroupBy(item => item.FilmId)
                .Select(group => group.First())
                .Select(item => new KinopoiskRelationInfo
                {
                    KinopoiskId = item.FilmId,
                    Name = FirstNotEmpty(item.NameRu, item.NameEn, item.NameOriginal),
                    OriginalName = FirstNotEmpty(item.NameOriginal, item.NameEn),
                    RelationType = item.RelationType.ToString(),
                    PosterUrl = FirstNotEmpty(item.PosterUrlPreview, item.PosterUrl),
                    KinopoiskUrl = $"https://www.kinopoisk.ru/film/{item.FilmId}/"
                })
                .OrderBy(item => RelationOrder(item.RelationType))
                .ThenBy(item => item.Name, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private static string NormalizeKinopoiskUrl(string? value, int kinopoiskId)
        {
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri)
                && string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(uri.Host, "www.kinopoisk.ru", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(uri.Host, "kinopoisk.ru", StringComparison.OrdinalIgnoreCase)))
            {
                return uri.AbsoluteUri;
            }

            return $"https://www.kinopoisk.ru/film/{kinopoiskId}/";
        }

        private static string NormalizeImdbUrl(string? imdbId)
        {
            if (string.IsNullOrWhiteSpace(imdbId)
                || !imdbId.StartsWith("tt", StringComparison.OrdinalIgnoreCase)
                || !imdbId.Skip(2).All(char.IsDigit))
            {
                return string.Empty;
            }

            return $"https://www.imdb.com/title/{imdbId.Trim()}/";
        }

        private static string NormalizeProfessionLabel(string? value)
            => string.IsNullOrWhiteSpace(value) ? "Другие участники" : value.Trim();

        private static int RelationOrder(string value)
            => value switch
            {
                "PREQUEL" => 10,
                "SEQUEL" => 20,
                "REMAKE" => 30,
                _ => 100
            };

        private static double? PositiveOrNull(double value)
            => value > 0 ? value : null;

        private static string FirstNotEmpty(params string?[] values)
            => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim()
                ?? string.Empty;
    }
}
