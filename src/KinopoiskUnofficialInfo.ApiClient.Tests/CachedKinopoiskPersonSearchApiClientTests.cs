using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class CachedKinopoiskPersonSearchApiClientTests
    {
        [Fact]
        public async Task ShouldCacheNormalizedPersonSearch()
        {
            var innerClient = new RecordingPersonSearchApiClient();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var first = await client.SearchPersons(" Винс Гиллиган ", 1, CancellationToken.None);
            var second = await client.SearchPersons("винс гиллиган", 1, CancellationToken.None);
            await client.SearchPersons("Винс Гиллиган", 2, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(2, innerClient.SearchRequestCount);
        }

        [Fact]
        public async Task ShouldCombineConcurrentPersonSearches()
        {
            var innerClient = new RecordingPersonSearchApiClient(TimeSpan.FromMilliseconds(50));
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var requests = new Task<PersonSearchResponse>[8];
            for (var index = 0; index < requests.Length; index++)
            {
                requests[index] = client.SearchPersons(
                    "Vince Gilligan",
                    1,
                    CancellationToken.None);
            }

            await Task.WhenAll(requests);

            Assert.Equal(1, innerClient.SearchRequestCount);
            foreach (var request in requests)
                Assert.Same(requests[0].Result, request.Result);
        }

        private sealed class RecordingPersonSearchApiClient : IKinopoiskApiClient, IKinopoiskPersonSearchApiClient
        {
            private readonly TimeSpan _delay;
            private int _searchRequestCount;

            public RecordingPersonSearchApiClient(TimeSpan? delay = null)
            {
                _delay = delay ?? TimeSpan.Zero;
            }

            public int SearchRequestCount => Volatile.Read(ref _searchRequestCount);

            public async Task<PersonSearchResponse> SearchPersons(
                string name,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                Interlocked.Increment(ref _searchRequestCount);
                if (_delay > TimeSpan.Zero)
                    await Task.Delay(_delay, cancellationToken ?? CancellationToken.None);

                return new PersonSearchResponse
                {
                    Total = 1,
                    Items = new[]
                    {
                        new PersonSearchItem
                        {
                            KinopoiskId = 66539,
                            NameRu = "Винс Гиллиган",
                            NameEn = "Vince Gilligan",
                            PosterUrl = "https://example.org/person.jpg"
                        }
                    }
                };
            }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

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
    }
}
