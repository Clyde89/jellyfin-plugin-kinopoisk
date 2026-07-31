using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Jellyfin.Data.Enums;
using Jellyfin.Extensions;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk
{
    public static class ApiModelExtensions
    {
        public static RemoteSearchResult ToRemoteSearchResult(this Film src)
        {
            if (src is null)
                return null;

            var configuration = GetConfiguration();
            var res = new RemoteSearchResult
            {
                Name = src.GetLocalName(),
                ImageUrl = src.PosterUrl,
                PremiereDate = src.GetPremiereDate(),
                ProductionYear = src.GetProductionYear(),
                Overview = src.GetOverview(configuration),
                SearchProviderName = Constants.ProviderName
            };
            res.SetProviderId(Constants.ProviderId, Convert.ToString(src.KinopoiskId, CultureInfo.InvariantCulture));
            if (!string.IsNullOrWhiteSpace(src.ImdbId))
                res.SetProviderId(MetadataProvider.Imdb, src.ImdbId.Trim());

            return res;
        }

        public static IEnumerable<RemoteSearchResult> ToRemoteSearchResults(this FilmSearchResponse src, ILogger logger)
        {
            if (src?.Films is null)
                return Enumerable.Empty<RemoteSearchResult>();

            return src.Films
                .Select(s => s.ToRemoteSearchResult(logger))
                .Where(s => s != null);
        }

        public static RemoteSearchResult ToRemoteSearchResult(this FilmSearchResponse_films src, ILogger logger)
        {
            try
            {
                if (src is null)
                    return null;

                var res = new RemoteSearchResult
                {
                    Name = src.GetLocalName(),
                    ImageUrl = src.PosterUrl,
                    PremiereDate = src.GetPremiereDate(),
                    ProductionYear = GetFirstYear(src.Year),
                    Overview = src.Description,
                    SearchProviderName = Constants.ProviderName
                };
                res.SetProviderId(Constants.ProviderId, Convert.ToString(src.FilmId, CultureInfo.InvariantCulture));

                return res;
            }
            catch (Exception exception)
            {
                logger.LogError(exception, "Результат поиска КиноПоиска не преобразован");
                return null;
            }
        }

        public static Series ToSeries(this Film src)
        {
            if (src is null)
                return null;

            var configuration = GetConfiguration();
            var result = new Series();
            FillCommonFilmInfo(src, result, configuration);

            if (configuration.EnableSeriesStatus)
                FillSeriesInfo(src, result);

            return result;
        }

        public static Movie ToMovie(this Film src)
        {
            if (src is null)
                return null;

            var result = new Movie();
            FillCommonFilmInfo(src, result, GetConfiguration());
            return result;
        }

        private static void FillCommonFilmInfo(
            Film source,
            BaseItem destination,
            PluginConfiguration configuration)
        {
            destination.SetProviderId(
                Constants.ProviderId,
                Convert.ToString(source.KinopoiskId, CultureInfo.InvariantCulture));
            destination.Name = source.GetLocalName();
            destination.OriginalTitle = source.GetOriginalNameIfNotSame();
            destination.PremiereDate = source.GetPremiereDate();
            destination.ProductionYear = source.GetProductionYear();

            if (!string.IsNullOrWhiteSpace(source.Slogan))
                destination.Tagline = source.Slogan.Trim();

            destination.Overview = source.GetOverview(configuration);

            if (source.Countries is not null)
            {
                destination.ProductionLocations = source.Countries
                    .Select(country => country?.Country1?.Trim())
                    .Where(country => !string.IsNullOrWhiteSpace(country))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray();
            }

            if (source.Genres is not null)
            {
                foreach (var genre in source.Genres
                    .Select(item => item?.Genre1?.Trim())
                    .Where(genre => !string.IsNullOrWhiteSpace(genre))
                    .Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    destination.AddGenre(genre);
                }
            }

            destination.OfficialRating = NormalizeOfficialRating(
                source.RatingAgeLimits,
                source.RatingMpaa);
            destination.CommunityRating = source.GetCommunityRating(
                configuration.CommunityRatingSource);
            destination.CriticRating = source.GetCriticRatingAsPercentage(
                configuration.CriticRatingSource);

            if (configuration.EnableRuntimeFallback && source.FilmLength > 0)
                destination.RunTimeTicks = TimeSpan.FromMinutes(source.FilmLength).Ticks;

            if (configuration.EnableKinopoiskHomePage
                && TryGetHttpUrl(source.WebUrl, out var homePageUrl))
            {
                destination.HomePageUrl = homePageUrl;
            }

            if (!string.IsNullOrWhiteSpace(source.ImdbId))
                destination.SetProviderId(MetadataProvider.Imdb, source.ImdbId.Trim());
        }

        private static void FillSeriesInfo(Film source, Series destination)
        {
            if (source.StartYear > 1900)
            {
                destination.ProductionYear = source.StartYear;
                if (!destination.PremiereDate.HasValue)
                    destination.PremiereDate = new DateTime(source.StartYear, 1, 1);
            }

            if (source.EndYear > 1900)
                destination.EndDate = new DateTime(source.EndYear, 12, 31);

            if (source.Completed
                || source.ProductionStatus == FilmProductionStatus.COMPLETED
                || source.EndYear > 1900)
            {
                destination.Status = SeriesStatus.Ended;
                return;
            }

            if (source.ProductionStatus is FilmProductionStatus.ANNOUNCED
                or FilmProductionStatus.PRE_PRODUCTION)
            {
                destination.Status = SeriesStatus.Unreleased;
                return;
            }

            destination.Status = SeriesStatus.Continuing;
        }

        public static float? GetCommunityRating(
            this Film source,
            CommunityRatingSource ratingSource)
        {
            if (source is null || ratingSource == CommunityRatingSource.Disabled)
                return null;

            var kinopoisk = NormalizeTenPointRating(source.RatingKinopoisk);
            var imdb = NormalizeTenPointRating(source.RatingImdb);

            return ratingSource switch
            {
                CommunityRatingSource.KinopoiskOnly => kinopoisk,
                CommunityRatingSource.ImdbWithKinopoiskFallback => imdb ?? kinopoisk,
                CommunityRatingSource.ImdbOnly => imdb,
                _ => kinopoisk ?? imdb
            };
        }

        public static float? GetCriticRatingAsPercentage(
            this Film source,
            CriticRatingSource ratingSource)
        {
            if (source is null || ratingSource == CriticRatingSource.Disabled)
                return null;

            var russian = NormalizeCriticPercentage(source.RatingRfCritics);
            var world = NormalizeCriticPercentage(source.RatingFilmCritics);

            return ratingSource switch
            {
                CriticRatingSource.RussianOnly => russian,
                CriticRatingSource.WorldWithRussianFallback => world ?? russian,
                CriticRatingSource.WorldOnly => world,
                _ => russian ?? world
            };
        }

        public static void ApplyPrecisePremiereDate(
            this BaseItem item,
            DistributionResponse distributions)
        {
            if (item is null)
                return;

            var premiereDate = distributions.GetPrecisePremiereDate();
            if (!premiereDate.HasValue)
                return;

            item.PremiereDate = premiereDate.Value;
            item.ProductionYear ??= premiereDate.Value.Year;
        }

        public static DateTime? GetPrecisePremiereDate(this DistributionResponse distributions)
        {
            if (distributions?.Items is null)
                return null;

            var parsed = distributions.Items
                .Where(item => item is not null && !item.ReRelease)
                .Select(item => new
                {
                    Item = item,
                    Date = item.Date.ParseDate()
                })
                .Where(item => item.Date.HasValue)
                .ToArray();

            if (parsed.Length == 0)
                return null;

            var worldPremiere = parsed
                .Where(item => item.Item.Type == DistributionType.WORLD_PREMIER)
                .OrderBy(item => item.Date)
                .Select(item => item.Date)
                .FirstOrDefault();
            if (worldPremiere.HasValue)
                return worldPremiere;

            var cinemaPremiere = parsed
                .Where(item => item.Item.Type == DistributionType.PREMIERE
                    && item.Item.SubType == DistributionSubType.CINEMA)
                .OrderBy(item => item.Date)
                .Select(item => item.Date)
                .FirstOrDefault();
            if (cinemaPremiere.HasValue)
                return cinemaPremiere;

            return parsed
                .Where(item => item.Item.Type == DistributionType.PREMIERE)
                .OrderBy(item => item.Date)
                .Select(item => item.Date)
                .FirstOrDefault()
                ?? parsed.OrderBy(item => item.Date).Select(item => item.Date).FirstOrDefault();
        }

        public static IEnumerable<RemoteImageInfo> ToRemoteImageInfos(this Film src)
        {
            if (src is null)
                return Enumerable.Empty<RemoteImageInfo>();

            var images = new List<RemoteImageInfo>();
            AddRemoteImage(images, src.PosterUrl, ImageType.Primary);
            AddRemoteImage(images, src.CoverUrl, ImageType.Primary);
            AddRemoteImage(images, src.LogoUrl, ImageType.Logo);

            return images
                .GroupBy(
                    image => $"{image.Type}:{image.Url}",
                    StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First());
        }

        private static void AddRemoteImage(
            ICollection<RemoteImageInfo> images,
            string url,
            ImageType imageType)
        {
            if (!TryGetHttpUrl(url, out var normalizedUrl))
                return;

            images.Add(new RemoteImageInfo
            {
                Type = imageType,
                Url = normalizedUrl,
                Language = Constants.ProviderMetadataLanguage,
                ProviderName = Constants.ProviderName
            });
        }

        public static IReadOnlyList<MediaUrl> ToMediaUrls(this VideoResponse src)
        {
            if (src?.Items is null || src.Items.Count < 1)
                return null;

            return src.Items
                .Select(item => item.ToMediaUrl())
                .Where(mediaUrl => mediaUrl is not null)
                .GroupBy(mediaUrl => mediaUrl.Url, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .ToList();
        }

        public static MediaUrl ToMediaUrl(this VideoResponse_items src)
        {
            if (src is null
                || !VideoResponse_itemsSite.YOUTUBE.Equals(src.Site)
                || string.IsNullOrWhiteSpace(src.Url))
            {
                return null;
            }

            var url = src.Url.SanitizeYoutubeLink();
            if (!TryGetHttpUrl(url, out var normalizedUrl))
                return null;

            return new MediaUrl
            {
                Name = string.IsNullOrWhiteSpace(src.Name)
                    ? "Трейлер КиноПоиска"
                    : src.Name.Trim(),
                Url = normalizedUrl
            };
        }

        public static string SanitizeYoutubeLink(this string src)
        {
            if (string.IsNullOrWhiteSpace(src))
                return string.Empty;

            return src.Trim()
                .Replace("http://", "https://", StringComparison.OrdinalIgnoreCase)
                .Replace("https://youtu.be/", "https://www.youtube.com/watch?v=", StringComparison.OrdinalIgnoreCase)
                .Replace("https://www.youtube.com/v/", "https://www.youtube.com/watch?v=", StringComparison.OrdinalIgnoreCase);
        }

        public static RemoteImageInfo ToRemoteImageInfo(this PersonResponse src)
        {
            if (src is null || !TryGetHttpUrl(src.PosterUrl, out var posterUrl))
                return null;

            return new RemoteImageInfo
            {
                Type = ImageType.Primary,
                Url = posterUrl,
                ProviderName = Constants.ProviderName
            };
        }

        public static PersonInfo ToPersonInfo(this StaffResponse src)
        {
            if (src is null)
                return null;

            var result = new PersonInfo
            {
                Name = src.NameRu,
                ImageUrl = src.PosterUrl,
                Role = src.ProfessionText,
                Type = src.ProfessionKey.ToPersonType()
            };
            if (string.IsNullOrWhiteSpace(result.Name))
                result.Name = src.NameEn ?? string.Empty;
            if (src.AdditionalProperties.TryGetValue("description", out var description))
                result.Role = description as string;

            result.SetProviderId(Constants.ProviderId, Convert.ToString(src.StaffId, CultureInfo.InvariantCulture));
            return result;
        }

        public static IEnumerable<PersonInfo> ToPersonInfos(this ICollection<StaffResponse> src)
        {
            if (src is null)
                return Enumerable.Empty<PersonInfo>();

            var result = src
                .Select(item => item.ToPersonInfo())
                .Where(item => item is not null && !string.IsNullOrWhiteSpace(item.Name))
                .ToArray();

            var sortOrder = 0;
            foreach (var item in result)
                item.SortOrder = ++sortOrder;

            return result;
        }

        public static PersonKind ToPersonType(this StaffResponseProfessionKey src)
        {
            return src switch
            {
                StaffResponseProfessionKey.ACTOR => PersonKind.Actor,
                StaffResponseProfessionKey.DIRECTOR
                    or StaffResponseProfessionKey.VOICE_DIRECTOR
                    or StaffResponseProfessionKey.OPERATOR => PersonKind.Director,
                StaffResponseProfessionKey.WRITER => PersonKind.Writer,
                StaffResponseProfessionKey.COMPOSER => PersonKind.Composer,
                StaffResponseProfessionKey.PRODUCER
                    or StaffResponseProfessionKey.PRODUCER_USSR => PersonKind.Producer,
                StaffResponseProfessionKey.EDITOR => PersonKind.Editor,
                StaffResponseProfessionKey.TRANSLATOR => PersonKind.Translator,
                _ => PersonKind.Unknown
            };
        }

        public static DateTime? ParseDate(this string src)
        {
            if (string.IsNullOrWhiteSpace(src))
                return null;

            var formats = new[]
            {
                "yyyy-MM-dd",
                "yyyy-MM-ddTHH:mm:ss",
                "yyyy-MM-ddTHH:mm:ssK",
                "o"
            };

            if (DateTime.TryParseExact(
                src.Trim(),
                formats,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces | DateTimeStyles.AssumeUniversal,
                out var result))
            {
                return result.Date;
            }

            return null;
        }

        public static DateTime? GetPremiereDate(this Film src)
        {
            var productionYear = src.GetProductionYear();
            return productionYear.HasValue
                ? new DateTime(productionYear.Value, 1, 1)
                : null;
        }

        public static int? GetProductionYear(this Film src)
        {
            if (src is null)
                return null;

            if (src.StartYear > 1900)
                return src.StartYear;

            return src.Year > 1900 ? src.Year : null;
        }

        public static DateTime? GetPremiereDate(this FilmSearchResponse_films src)
        {
            var firstYear = GetFirstYear(src.Year);
            return firstYear.HasValue
                ? new DateTime(firstYear.Value, 1, 1)
                : null;
        }

        public static string GetOverview(
            this Film src,
            PluginConfiguration configuration)
        {
            if (src is null)
                return null;

            if (!string.IsNullOrWhiteSpace(src.Description))
                return src.Description.Trim();

            if (configuration?.EnableShortDescriptionFallback != false
                && !string.IsNullOrWhiteSpace(src.ShortDescription))
            {
                return src.ShortDescription.Trim();
            }

            return null;
        }

        public static string GetLocalName(this Film src)
        {
            var result = src?.NameRu;
            if (string.IsNullOrWhiteSpace(result))
                result = src?.NameOriginal;
            if (string.IsNullOrWhiteSpace(result))
                result = src?.NameEn;
            return result?.Trim();
        }

        public static string GetLocalName(this FilmSearchResponse_films src)
        {
            var result = src?.NameRu;
            if (string.IsNullOrWhiteSpace(result))
                result = src?.NameEn;
            return result?.Trim();
        }

        public static string GetOriginalName(this Film src)
            => src?.NameOriginal
                ?? (src.IsRussianSpokenOriginated()
                    ? src?.NameRu
                    : src?.NameEn);

        public static string GetOriginalNameIfNotSame(this Film src)
        {
            var localName = src.GetLocalName();
            var originalName = src.GetOriginalName()?.Trim();
            if (!string.IsNullOrWhiteSpace(originalName)
                && !string.Equals(localName, originalName, StringComparison.OrdinalIgnoreCase))
            {
                return originalName;
            }

            return string.Empty;
        }

        public static bool IsRussianSpokenOriginated(this Film src)
            => src?.Countries?.IsRussianSpokenOriginated() ?? false;

        public static bool IsRussianSpokenOriginated(this IEnumerable<Country> src)
        {
            if (src is null)
                return false;

            return src.Any(country => string.Equals(
                country?.Country1?.Trim(),
                "Россия",
                StringComparison.OrdinalIgnoreCase));
        }

        public static int? GetFirstYear(string years)
        {
            if (string.IsNullOrWhiteSpace(years)
                || string.Equals(years.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            var normalized = years.Trim();
            var digits = new string(normalized.TakeWhile(char.IsDigit).Take(4).ToArray());
            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                && result > 1900
                ? result
                : null;
        }

        public static bool IsСontinuing(string years)
            => years?.EndsWith("-...", StringComparison.Ordinal) ?? false;

        public static int? GetLastYear(string years)
        {
            if (string.IsNullOrWhiteSpace(years))
                return null;

            var normalized = years.Trim();
            var digits = new string(normalized
                .Reverse()
                .TakeWhile(char.IsDigit)
                .Take(4)
                .Reverse()
                .ToArray());

            return int.TryParse(digits, NumberStyles.None, CultureInfo.InvariantCulture, out var result)
                && result > 1900
                ? result
                : null;
        }

        public static Person ToPerson(this PersonResponse src)
        {
            if (src is null)
                return null;

            var result = new Person
            {
                Name = src.GetLocalName(),
                PremiereDate = src.Birthday.ParseDate(),
                EndDate = src.Death.ParseDate()
            };

            if (!string.IsNullOrWhiteSpace(src.Birthplace))
                result.ProductionLocations = new[] { src.Birthplace.Trim() };

            return result;
        }

        public static string GetLocalName(this PersonResponse src)
        {
            var result = src?.NameRu;
            if (string.IsNullOrWhiteSpace(result))
                result = src?.NameEn;
            return result?.Trim();
        }

        private static PluginConfiguration GetConfiguration()
            => Plugin.Instance?.Configuration ?? new PluginConfiguration();

        private static float? NormalizeTenPointRating(double value)
        {
            if (value <= 0 || value > 10)
                return null;

            return (float)value;
        }

        private static float? NormalizeCriticPercentage(double value)
        {
            if (value <= 0)
                return null;

            var percentage = value <= 10 ? value * 10 : value;
            return (float)Math.Clamp(percentage, 0, 100);
        }

        private static string NormalizeOfficialRating(
            string ageRating,
            string mpaaRating)
        {
            if (!string.IsNullOrWhiteSpace(ageRating))
            {
                var digits = new string(ageRating.Where(char.IsDigit).ToArray());
                if (!string.IsNullOrWhiteSpace(digits))
                    return digits + "+";
            }

            return string.IsNullOrWhiteSpace(mpaaRating)
                ? null
                : mpaaRating.Trim().ToUpperInvariant();
        }

        private static bool TryGetHttpUrl(string value, out string normalizedUrl)
        {
            normalizedUrl = null;
            if (!Uri.TryCreate(value?.Trim(), UriKind.Absolute, out var uri)
                || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                return false;
            }

            normalizedUrl = uri.AbsoluteUri;
            return true;
        }
    }
}
