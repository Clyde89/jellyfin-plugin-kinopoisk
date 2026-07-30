using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class CachedKinopoiskSeasonApiClientTests
    {
        [Fact]
        public async Task ShouldCacheSeasonsBySeriesIdentifier()
        {
            var innerClient = new RecordingSeasonApiClient();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var first = await client.GetSeasons(1046206, CancellationToken.None);
            var second = await client.GetSeasons(1046206, CancellationToken.None);
            await client.GetSeasons(45319, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(2, innerClient.RequestCount);
        }

        [Fact]
        public async Task ShouldCombineConcurrentSeasonRequests()
        {
            var innerClient = new RecordingSeasonApiClient(TimeSpan.FromMilliseconds(50));
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var requests = new Task<SeasonResponse>[8];
            for (var index = 0; index < requests.Length; index++)
                requests[index] = client.GetSeasons(1046206, CancellationToken.None);

            await Task.WhenAll(requests);

            Assert.Equal(1, innerClient.RequestCount);
            foreach (var request in requests)
                Assert.Same(requests[0].Result, request.Result);
        }

        private sealed class RecordingSeasonApiClient : IKinopoiskApiClient, IKinopoiskSeasonApiClient
        {
            private readonly TimeSpan _delay;
            private int _requestCount;

            public RecordingSeasonApiClient(TimeSpan? delay = null)
            {
                _delay = delay ?? TimeSpan.Zero;
            }

            public int RequestCount => Volatile.Read(ref _requestCount);

            public async Task<SeasonResponse> GetSeasons(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                Interlocked.Increment(ref _requestCount);
                if (_delay > TimeSpan.Zero)
                    await Task.Delay(_delay, cancellationToken ?? CancellationToken.None);

                return new SeasonResponse
                {
                    Total = 1,
                    Items = new[]
                    {
                        new Season
                        {
                            Number = 1,
                            Episodes = Array.Empty<Episode>()
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
