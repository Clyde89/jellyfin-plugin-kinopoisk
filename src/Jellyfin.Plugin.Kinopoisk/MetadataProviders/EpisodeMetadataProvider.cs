using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;
using ApiEpisode = KinopoiskUnofficialInfo.ApiClient.Episode;
using JellyfinEpisode = MediaBrowser.Controller.Entities.TV.Episode;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    /// <summary>
    /// Загружает сведения об эпизодах сериалов КиноПоиска.
    /// </summary>
    public class EpisodeMetadataProvider : BaseMetadataProvider, IRemoteMetadataProvider<JellyfinEpisode, EpisodeInfo>
    {
        private readonly IKinopoiskSeasonApiClient _seasonApiClient;
        private readonly ILogger<EpisodeMetadataProvider> _logger;

        public EpisodeMetadataProvider(
            IKinopoiskSeasonApiClient seasonApiClient,
            ILogger<EpisodeMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
            _seasonApiClient = seasonApiClient ?? throw new ArgumentNullException(nameof(seasonApiClient));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MetadataResult<JellyfinEpisode>> GetMetadata(
            EpisodeInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<JellyfinEpisode>
            {
                Provider = Constants.ProviderName,
                ResultLanguage = Constants.ProviderMetadataLanguage
            };

            if (info is null
                || info.IsMissingEpisode
                || !info.IndexNumber.HasValue
                || !TryGetSeriesId(info, out var seriesId))
            {
                return result;
            }

            var seasonNumber = info.ParentIndexNumber ?? 1;
            var response = await _seasonApiClient
                .GetSeasons(seriesId, cancellationToken)
                .ConfigureAwait(false);
            var season = response?.Items?.FirstOrDefault(item => item.Number == seasonNumber);
            if (season?.Episodes is null)
                return result;

            var endNumber = info.IndexNumberEnd ?? info.IndexNumber.Value;
            var episodes = season.Episodes
                .Where(episode =>
                    episode.EpisodeNumber >= info.IndexNumber.Value
                    && episode.EpisodeNumber <= endNumber)
                .OrderBy(episode => episode.EpisodeNumber)
                .ToArray();
            if (episodes.Length == 0)
                return result;

            var premiereDate = episodes
                .Select(episode => episode.ReleaseDate.ParseDate())
                .Where(date => date.HasValue)
                .Select(date => (DateTime?)date.Value)
                .OrderBy(date => date)
                .FirstOrDefault();

            var localNames = episodes
                .Select(GetLocalName)
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var originalNames = episodes
                .Select(episode => episode.NameEn?.Trim())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .ToArray();
            var overviews = episodes
                .Select(episode => episode.Synopsis?.Trim())
                .Where(overview => !string.IsNullOrWhiteSpace(overview))
                .ToArray();

            var item = new JellyfinEpisode
            {
                IndexNumber = info.IndexNumber,
                IndexNumberEnd = info.IndexNumberEnd,
                ParentIndexNumber = seasonNumber,
                Name = localNames.Length > 0
                    ? string.Join(" / ", localNames)
                    : $"Эпизод {info.IndexNumber.Value}",
                OriginalTitle = originalNames.Length > 0
                    ? string.Join(" / ", originalNames)
                    : string.Empty,
                Overview = overviews.Length > 0
                    ? string.Join(Environment.NewLine + Environment.NewLine, overviews)
                    : null,
                PremiereDate = premiereDate,
                ProductionYear = premiereDate?.Year
            };

            if (string.Equals(item.Name, item.OriginalTitle, StringComparison.OrdinalIgnoreCase))
                item.OriginalTitle = string.Empty;

            result.Item = item;
            result.HasMetadata = true;
            result.QueriedById = true;

            _logger.LogDebug(
                "Для сериала КиноПоиска {KinopoiskId} подготовлен эпизод S{SeasonNumber}E{EpisodeNumber}-{EpisodeNumberEnd}",
                seriesId,
                seasonNumber,
                info.IndexNumber.Value,
                endNumber);

            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            EpisodeInfo searchInfo,
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
                    IndexNumberEnd = metadata.Item.IndexNumberEnd,
                    ParentIndexNumber = metadata.Item.ParentIndexNumber,
                    PremiereDate = metadata.Item.PremiereDate,
                    ProductionYear = metadata.Item.ProductionYear,
                    SearchProviderName = Constants.ProviderName
                }
            };
        }

        private static bool TryGetSeriesId(EpisodeInfo info, out int seriesId)
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

        private static string GetLocalName(ApiEpisode episode)
        {
            if (!string.IsNullOrWhiteSpace(episode.NameRu))
                return episode.NameRu.Trim();

            return string.IsNullOrWhiteSpace(episode.NameEn)
                ? null
                : episode.NameEn.Trim();
        }
    }
}
