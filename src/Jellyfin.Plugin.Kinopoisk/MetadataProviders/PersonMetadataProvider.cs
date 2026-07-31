using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Providers;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.MetadataProviders
{
    public class PersonMetadataProvider : BaseMetadataProvider, IRemoteMetadataProvider<Person, PersonLookupInfo>
    {
        private readonly IKinopoiskApiClient _apiClient;
        private readonly IKinopoiskPersonSearchApiClient _personSearchApiClient;
        private readonly IProviderIdResolver<PersonLookupInfo> _providerIdResolver;
        private readonly ILogger<PersonMetadataProvider> _logger;

        public PersonMetadataProvider(
            IKinopoiskApiClient apiClient,
            IKinopoiskPersonSearchApiClient personSearchApiClient,
            IProviderIdResolver<PersonLookupInfo> providerIdResolver,
            ILogger<PersonMetadataProvider> logger,
            IHttpClientFactory httpClientFactory)
            : base(httpClientFactory)
        {
            _apiClient = apiClient ?? throw new ArgumentNullException(nameof(apiClient));
            _personSearchApiClient = personSearchApiClient ?? throw new ArgumentNullException(nameof(personSearchApiClient));
            _providerIdResolver = providerIdResolver ?? throw new ArgumentNullException(nameof(providerIdResolver));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<MetadataResult<Person>> GetMetadata(
            PersonLookupInfo info,
            CancellationToken cancellationToken)
        {
            var result = new MetadataResult<Person>
            {
                QueriedById = true,
                Provider = Constants.ProviderName,
                ResultLanguage = Constants.ProviderMetadataLanguage
            };

            if (Plugin.Instance?.Configuration.EnablePeopleMetadata == false)
                return result;

            var (resolveResult, kinopoiskId) = await _providerIdResolver
                .TryResolve(info, cancellationToken)
                .ConfigureAwait(false);
            if (!resolveResult)
                return result;

            var person = await _apiClient
                .GetPerson(kinopoiskId, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();

            result.Item = person.ToEnrichedPerson();
            result.HasMetadata = result.Item != null;
            return result;
        }

        public async Task<IEnumerable<RemoteSearchResult>> GetSearchResults(
            PersonLookupInfo searchInfo,
            CancellationToken cancellationToken)
        {
            if (Plugin.Instance?.Configuration.EnablePeopleMetadata == false
                || searchInfo is null
                || string.IsNullOrWhiteSpace(searchInfo.Name))
            {
                return Enumerable.Empty<RemoteSearchResult>();
            }

            var requestedName = searchInfo.Name.Trim();
            var firstPage = await _personSearchApiClient
                .SearchPersons(requestedName, 1, cancellationToken)
                .ConfigureAwait(false);

            var pages = new List<PersonSearchResponse> { firstPage };
            if (firstPage?.Items != null && firstPage.Total > firstPage.Items.Count)
            {
                pages.Add(await _personSearchApiClient
                    .SearchPersons(requestedName, 2, cancellationToken)
                    .ConfigureAwait(false));
            }

            cancellationToken.ThrowIfCancellationRequested();

            var results = pages
                .Where(page => page?.Items != null)
                .SelectMany(page => page.Items)
                .Where(person => person != null && person.KinopoiskId > 0)
                .GroupBy(person => person.KinopoiskId)
                .Select(group => group.First())
                .OrderByDescending(person => person.HasExactName(requestedName))
                .ThenBy(person => person.GetLocalName(), StringComparer.OrdinalIgnoreCase)
                .Select(person => person.ToRemoteSearchResult())
                .Where(result => result != null)
                .ToArray();

            _logger.LogDebug(
                "По запросу персоны '{PersonName}' подготовлено {ResultCount} результатов",
                requestedName,
                results.Length);

            return results;
        }
    }
}
