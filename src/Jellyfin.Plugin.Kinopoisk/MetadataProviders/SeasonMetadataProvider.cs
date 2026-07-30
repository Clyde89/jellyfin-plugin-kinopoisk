using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using ApiSeason = KinopoiskUnofficialInfo.ApiClient.Season;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    /// <summary>
    /// Загружает сведения о сезонах сериалов КиноПоиска.
    /// </summary>
    public class SeasonMetadataProvider : BaseMetadataProvider, IRemoteMetadataProvider<Season, SeasonInfo>
    {
        private readonly IKinopoiskSeasonApiClient _seasonApiClient;
        private readonly ILogger<SeasonMetadataProvider> _logger;

        public SeasonMetadataProvider(
            IKinopoiskSeasonApiClient seasonApiClient,
            ILogger<SeasonMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
            _seasonApiClient = seasonApiClient ?? throw new ArgumentNullException(nameof(seasonApiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MetadataResult<Season>> GetMetadata(
            SeasonInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Season>
            {
                Provider = Constants.ProviderName,
                ResultLanguage = Constants.ProviderMetadataLanguage
            };

            if (!TryGetSeriesId(info, out var seriesId) || !info.IndexNumber.HasValue)
                return result;

            var response = await _seasonApiClient
                .GetSeasons(seriesId, cancellationToken)
                .ConfigureAwait(false);
            var apiSeason = response?.Items?.FirstOrDefault(season =>
                season.Number == info.IndexNumber.Value);
            if (apiSeason is null)
                return result;

            var premiereDate = GetPremiereDate(apiSeason);
            var episodeCount = apiSeason.Episodes?.Count ?? 0;
            result.Item = new Season
            {
                IndexNumber = apiSeason.Number,
                Name = apiSeason.Number == 0
                    ? "Специальные материалы"
                    : $"Сезон {apiSeason.Number}",
                PremiereDate = premiereDate,
                ProductionYear = premiereDate?.Year,
                Overview = episodeCount > 0
                    ? $"Сезон содержит {episodeCount.ToString(CultureInfo.InvariantCulture)} эпизодов."
                    : null
            };
            result.HasMetadata = true;
            result.QueriedById = true;

            _logger.LogDebug(
                "Для сериала КиноПоиска {KinopoiskId} подготовлен сезон {SeasonNumber} с {EpisodeCount} эпизодами",
                seriesId,
                apiSeason.Number,
                episodeCount);

            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            SeasonInfo searchInfo,
            CancellationToken cancellationToken)
        {
            var metadata = await GetMetadata(searchInfo, cancellationToken).ConfigureAwait(false);
            if (!metadata.HasMetadata || metadata.Item is null)
                return Enumerable.Empty<RemoteSearchResult>();

            return new[]
            {
                new RemoteSearchResult
                {
                    Name = metadata.Item.Name,
                    IndexNumber = metadata.Item.IndexNumber,
                    PremiereDate = metadata.Item.PremiereDate,
                    ProductionYear = metadata.Item.ProductionYear,
                    SearchProviderName = Constants.ProviderName
                }
            };
        }

        private static bool TryGetSeriesId(SeasonInfo info, out int seriesId)
        {
            seriesId = 0;
            if (info?.SeriesProviderIds is null
                || !info.SeriesProviderIds.TryGetValue(Constants.ProviderId, out var value))
            {
                return false;
            }

            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out seriesId)
                && seriesId > 0;
        }

        private static DateTime? GetPremiereDate(ApiSeason season)
        {
            return season.Episodes?
                .Select(episode => episode.ReleaseDate.ParseDate())
                .Where(date => date.HasValue)
                .Select(date => date.Value)
                .OrderBy(date => date)
                .Cast<DateTime?>()
                .FirstOrDefault();
        }
    }
}
