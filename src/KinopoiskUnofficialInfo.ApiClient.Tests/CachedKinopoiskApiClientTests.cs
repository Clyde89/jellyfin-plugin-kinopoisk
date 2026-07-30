using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class CachedKinopoiskApiClientTests
    {
        [Fact]
        public async Task ShouldReuseCachedFilmResponse()
        {
            var innerClient = new FakeKinopoiskApiClient
            {
                GetSingleFilmHandler = _ => Task.FromResult(CreateFilm())
            };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(innerClient, cache);

            var first = await client.GetSingleFilm(5273, CancellationToken.None);
            var second = await client.GetSingleFilm(5273, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(1, innerClient.GetSingleFilmCalls);
        }

        [Fact]
        public async Task ShouldShareConcurrentFilmRequest()
        {
            var requestStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var requestReleased = new TaskCompletionSource<Film>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var innerClient = new FakeKinopoiskApiClient
            {
                GetSingleFilmHandler = _ =>
                {
                    requestStarted.TrySetResult(true);
                    return requestReleased.Task;
                }
            };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(innerClient, cache);

            var requests = Enumerable.Range(0, 8)
                .Select(_ => client.GetSingleFilm(5273, CancellationToken.None))
                .ToArray();

            await requestStarted.Task;
            Assert.Equal(1, innerClient.GetSingleFilmCalls);

            requestReleased.SetResult(CreateFilm());
            var results = await Task.WhenAll(requests);

            Assert.All(results, result => Assert.Equal(5273, result.KinopoiskId));
            Assert.Equal(1, innerClient.GetSingleFilmCalls);
        }

        [Fact]
        public async Task ShouldNotCacheFailedRequest()
        {
            var innerClient = new FakeKinopoiskApiClient();
            innerClient.GetSingleFilmHandler = _ =>
            {
                if (innerClient.GetSingleFilmCalls == 1)
                    throw new InvalidOperationException("Временная ошибка");

                return Task.FromResult(CreateFilm());
            };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(innerClient, cache);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                client.GetSingleFilm(5273, CancellationToken.None));

            var result = await client.GetSingleFilm(5273, CancellationToken.None);

            Assert.Equal(5273, result.KinopoiskId);
            Assert.Equal(2, innerClient.GetSingleFilmCalls);
        }

        [Fact]
        public async Task ShouldCacheEmptyFilteredSearchResponse()
        {
            var innerClient = new FakeKinopoiskApiClient
            {
                SearchFilmsHandler = (_, _) => Task.FromResult(
                    new FilteredFilmSearchResponse
                    {
                        Total = 0,
                        TotalPages = 0,
                        Items = Array.Empty<FilteredFilmSearchItem>()
                    })
            };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(innerClient, cache);
            var query = new FilmSearchQuery
            {
                Keyword = "Несуществующий фильм",
                YearFrom = 2026,
                YearTo = 2026,
                Type = "FILM",
                Page = 1
            };

            await client.SearchFilms(query, CancellationToken.None);
            await client.SearchFilms(query, CancellationToken.None);

            Assert.Equal(1, innerClient.SearchFilmsCalls);
        }

        [Fact]
        public async Task ShouldCancelOneWaiterWithoutCancellingSharedRequest()
        {
            var requestStarted = new TaskCompletionSource<bool>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var requestReleased = new TaskCompletionSource<Film>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            var innerClient = new FakeKinopoiskApiClient
            {
                GetSingleFilmHandler = _ =>
                {
                    requestStarted.TrySetResult(true);
                    return requestReleased.Task;
                }
            };
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(innerClient, cache);
            using var cancellationTokenSource = new CancellationTokenSource();

            var cancelledRequest = client.GetSingleFilm(5273, cancellationTokenSource.Token);
            var successfulRequest = client.GetSingleFilm(5273, CancellationToken.None);

            await requestStarted.Task;
            cancellationTokenSource.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelledRequest);

            requestReleased.SetResult(CreateFilm());
            var result = await successfulRequest;

            Assert.Equal(5273, result.KinopoiskId);
            Assert.Equal(1, innerClient.GetSingleFilmCalls);
        }

        private static CachedKinopoiskApiClient CreateClient(
            IKinopoiskApiClient innerClient,
            IMemoryCache cache)
        {
            return new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);
        }

        private static Film CreateFilm()
        {
            return new Film
            {
                KinopoiskId = 5273,
                NameRu = "Шрэк 2",
                NameOriginal = "Shrek 2",
                Year = 2004,
                Type = FilmType.FILM
            };
        }

        private sealed class FakeKinopoiskApiClient : IFilteredKinopoiskApiClient
        {
            public Func<CancellationToken, Task<Film>> GetSingleFilmHandler { get; set; }

            public Func<FilmSearchQuery, CancellationToken, Task<FilteredFilmSearchResponse>> SearchFilmsHandler { get; set; }

            public int GetSingleFilmCalls { get; private set; }

            public int SearchFilmsCalls { get; private set; }

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetSingleFilmCalls++;
                return GetSingleFilmHandler?.Invoke(cancellationToken ?? CancellationToken.None)
                    ?? Task.FromResult(CreateFilm());
            }

            public Task<FilteredFilmSearchResponse> SearchFilms(
                FilmSearchQuery query,
                CancellationToken? cancellationToken = null)
            {
                SearchFilmsCalls++;
                return SearchFilmsHandler?.Invoke(
                    query,
                    cancellationToken ?? CancellationToken.None)
                    ?? Task.FromResult(new FilteredFilmSearchResponse());
            }

            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }
        }
    }
}
