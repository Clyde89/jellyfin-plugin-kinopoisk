using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PersonMetadataProviderTests
    {
        [Fact]
        public async Task ShouldFillExtendedPersonMetadata()
        {
            var apiClient = new FakeKinopoiskApiClient
            {
                Person = new PersonResponse
                {
                    PersonId = 66539,
                    NameRu = "Винс Гиллиган",
                    NameEn = "Vince Gilligan",
                    Birthday = "1967-02-10T00:00:00.0000000Z",
                    Death = string.Empty,
                    Birthplace = "Ричмонд, Вирджиния, США",
                    Deathplace = string.Empty,
                    Profession = "Сценарист, Продюсер, Режиссёр",
                    Facts = new[] { "Создатель сериала «Во все тяжкие»." }
                }
            };
            var provider = CreateProvider(
                apiClient,
                new EmptyPersonSearchApiClient(),
                new FixedProviderIdResolver(true, 66539));

            var result = await provider.GetMetadata(
                new PersonLookupInfo { Name = "Винс Гиллиган" },
                CancellationToken.None);

            Assert.True(result.HasMetadata);
            Assert.NotNull(result.Item);
            Assert.Equal("Винс Гиллиган", result.Item.Name);
            Assert.Equal("Vince Gilligan", result.Item.OriginalTitle);
            Assert.Equal(1967, result.Item.PremiereDate?.Year);
            Assert.Equal("Ричмонд, Вирджиния, США", Assert.Single(result.Item.ProductionLocations));
            Assert.Contains("Профессия:", result.Item.Overview);
            Assert.Contains("Во все тяжкие", result.Item.Overview);
            Assert.Equal("66539", GetProviderId(result.Item, Constants.ProviderId));
        }

        [Fact]
        public async Task ShouldReturnBothPagesAndPrioritizeExactName()
        {
            var searchClient = new FakePersonSearchApiClient
            {
                FirstPage = new PersonSearchResponse
                {
                    Total = 3,
                    Items = new[]
                    {
                        CreatePerson(2, "Винсент Гиллиган", "Vincent Gilligan"),
                        CreatePerson(1, "Винс Гиллиган", "Vince Gilligan")
                    }
                },
                SecondPage = new PersonSearchResponse
                {
                    Total = 3,
                    Items = new[]
                    {
                        CreatePerson(1, "Винс Гиллиган", "Vince Gilligan"),
                        CreatePerson(3, "Другой человек", "Vince Gilligan")
                    }
                }
            };
            var provider = CreateProvider(
                new FakeKinopoiskApiClient(),
                searchClient,
                new FixedProviderIdResolver(false, 0));

            var results = (await provider.GetSearchResults(
                new PersonLookupInfo { Name = "Винс Гиллиган" },
                CancellationToken.None)).ToArray();

            Assert.Equal(3, results.Length);
            Assert.Equal("Винс Гиллиган", results[0].Name);
            Assert.Equal("1", GetProviderId(results[0], Constants.ProviderId));
            Assert.Equal(2, searchClient.RequestedPages.Count);
            Assert.Contains(1, searchClient.RequestedPages);
            Assert.Contains(2, searchClient.RequestedPages);
        }

        [Fact]
        public async Task ShouldSkipSearchForEmptyName()
        {
            var searchClient = new FakePersonSearchApiClient();
            var provider = CreateProvider(
                new FakeKinopoiskApiClient(),
                searchClient,
                new FixedProviderIdResolver(false, 0));

            var results = await provider.GetSearchResults(
                new PersonLookupInfo { Name = " " },
                CancellationToken.None);

            Assert.Empty(results);
            Assert.Empty(searchClient.RequestedPages);
        }

        private static PersonSearchItem CreatePerson(int id, string nameRu, string nameEn)
        {
            return new PersonSearchItem
            {
                KinopoiskId = id,
                NameRu = nameRu,
                NameEn = nameEn,
                PosterUrl = $"https://example.org/{id}.jpg"
            };
        }

        private static PersonMetadataProvider CreateProvider(
            IKinopoiskApiClient apiClient,
            IKinopoiskPersonSearchApiClient searchApiClient,
            IProviderIdResolver<PersonLookupInfo> resolver)
        {
            return new PersonMetadataProvider(
                apiClient,
                searchApiClient,
                resolver,
                NullLogger<PersonMetadataProvider>.Instance,
                new FakeHttpClientFactory());
        }

        private static string GetProviderId(IHasProviderIds source, string providerName)
        {
            Assert.True(source.TryGetProviderId(providerName, out var providerId));
            return providerId;
        }

        private sealed class FixedProviderIdResolver : IProviderIdResolver<PersonLookupInfo>
        {
            private readonly bool _isSuccess;
            private readonly int _providerId;

            public FixedProviderIdResolver(bool isSuccess, int providerId)
            {
                _isSuccess = isSuccess;
                _providerId = providerId;
            }

            public Task<(bool IsSuccess, int ProviderId)> TryResolve(
                PersonLookupInfo info,
                CancellationToken? ct = null)
            {
                return Task.FromResult((_isSuccess, _providerId));
            }
        }

        private sealed class FakeHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                return new HttpClient();
            }
        }

        private sealed class FakeKinopoiskApiClient : IKinopoiskApiClient
        {
            public PersonResponse Person { get; set; }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
                => Task.FromResult(Person);

            public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();
        }

        private sealed class EmptyPersonSearchApiClient : IKinopoiskPersonSearchApiClient
        {
            public Task<PersonSearchResponse> SearchPersons(
                string name,
                int page = 1,
                CancellationToken? cancellationToken = null)
                => Task.FromResult(new PersonSearchResponse());
        }

        private sealed class FakePersonSearchApiClient : IKinopoiskPersonSearchApiClient
        {
            public PersonSearchResponse FirstPage { get; set; } = new();

            public PersonSearchResponse SecondPage { get; set; } = new();

            public ICollection<int> RequestedPages { get; } = new List<int>();

            public Task<PersonSearchResponse> SearchPersons(
                string name,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                RequestedPages.Add(page);
                return Task.FromResult(page == 2 ? SecondPage : FirstPage);
            }
        }
    }
}
