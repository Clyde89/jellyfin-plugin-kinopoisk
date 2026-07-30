using System;
using System.Collections.Generic;
using System.Linq;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Providers;

namespace Jellyfin.Plugin.Kinopoisk
{
    /// <summary>
    /// Преобразует модели персон КиноПоиска в модели Jellyfin.
    /// </summary>
    public static class PersonApiModelExtensions
    {
        public static Person ToEnrichedPerson(this PersonResponse source)
        {
            if (source is null || source.PersonId < 1)
                return null;

            var localName = source.GetLocalName();
            if (string.IsNullOrWhiteSpace(localName))
                return null;

            var person = new Person
            {
                Name = localName,
                OriginalTitle = GetOriginalName(source, localName),
                PremiereDate = source.Birthday.ParseDate(),
                EndDate = source.Death.ParseDate(),
                Overview = BuildOverview(source)
            };

            if (!string.IsNullOrWhiteSpace(source.Birthplace))
                person.ProductionLocations = new[] { source.Birthplace.Trim() };

            person.SetProviderId(Constants.ProviderId, source.PersonId.ToString());
            return person;
        }

        public static RemoteSearchResult ToRemoteSearchResult(this PersonSearchItem source)
        {
            if (source is null || source.KinopoiskId < 1)
                return null;

            var localName = GetLocalName(source);
            if (string.IsNullOrWhiteSpace(localName))
                return null;

            var result = new RemoteSearchResult
            {
                Name = localName,
                ImageUrl = source.PosterUrl,
                SearchProviderName = Constants.ProviderName
            };

            result.SetProviderId(Constants.ProviderId, source.KinopoiskId.ToString());
            return result;
        }

        public static string GetLocalName(this PersonSearchItem source)
        {
            if (source is null)
                return null;

            if (!string.IsNullOrWhiteSpace(source.NameRu))
                return source.NameRu.Trim();

            return string.IsNullOrWhiteSpace(source.NameEn)
                ? null
                : source.NameEn.Trim();
        }

        public static bool HasExactName(this PersonSearchItem source, string requestedName)
        {
            if (source is null || string.IsNullOrWhiteSpace(requestedName))
                return false;

            var normalizedName = requestedName.Trim();
            return string.Equals(source.NameRu?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(source.NameEn?.Trim(), normalizedName, StringComparison.OrdinalIgnoreCase);
        }

        private static string GetOriginalName(PersonResponse source, string localName)
        {
            if (string.IsNullOrWhiteSpace(source.NameEn))
                return string.Empty;

            var originalName = source.NameEn.Trim();
            return string.Equals(localName, originalName, StringComparison.OrdinalIgnoreCase)
                ? string.Empty
                : originalName;
        }

        private static string BuildOverview(PersonResponse source)
        {
            var sections = new List<string>();

            if (!string.IsNullOrWhiteSpace(source.Profession))
                sections.Add($"Профессия: {source.Profession.Trim()}.");

            if (!string.IsNullOrWhiteSpace(source.Deathplace))
                sections.Add($"Место смерти: {source.Deathplace.Trim()}.");

            if (source.Facts != null)
            {
                var facts = source.Facts
                    .Where(fact => !string.IsNullOrWhiteSpace(fact))
                    .Select(fact => fact.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .Take(20)
                    .ToArray();

                if (facts.Length > 0)
                    sections.Add(string.Join(Environment.NewLine, facts));
            }

            return sections.Count == 0
                ? null
                : string.Join(Environment.NewLine + Environment.NewLine, sections);
        }
    }
}
