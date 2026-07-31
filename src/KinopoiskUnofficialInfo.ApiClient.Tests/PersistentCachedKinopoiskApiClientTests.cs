using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class PersistentCachedKinopoiskApiClientTests
    {
        [Fact]
        public async Task ShouldReusePersistentFilmResponseAfterMemoryCacheRecreation()
        {
            var cachePath = CreateTemporaryCachePath();

            try
            {
                var diagnostics = new KinopoiskDiagnostics();
                var options = CreateOptions(cachePath);
                var firstInnerClient = new FakeKinopoiskApiClient();

                using (var firstMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var firstClient = CreateClient(
                        firstInnerClient,
                        firstMemoryCache,
                        options,
                        diagnostics);

                    var firstResult = await firstClient.GetSingleFilm(5273, CancellationToken.None);
                    Assert.Equal(5273, firstResult.KinopoiskId);
                }

                var secondInnerClient = new FakeKinopoiskApiClient
                {
                    GetSingleFilmHandler = _ => throw new InvalidOperationException(
                        "API не должен вызываться при наличии актуального дискового кэша.")
                };

                using (var secondMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var secondClient = CreateClient(
                        secondInnerClient,
                        secondMemoryCache,
                        options,
                        diagnostics);

                    var secondResult = await secondClient.GetSingleFilm(5273, CancellationToken.None);

                    Assert.Equal(5273, secondResult.KinopoiskId);
                    Assert.Equal("Шрэк 2", secondResult.NameRu);
                }

                var snapshot = diagnostics.GetSnapshot();
                Assert.Equal(1, firstInnerClient.GetSingleFilmCalls);
                Assert.Equal(0, secondInnerClient.GetSingleFilmCalls);
                Assert.Equal(1, snapshot.PersistentCacheHits);
                Assert.Equal(1, snapshot.PersistentCacheWrites);
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Fact]
        public async Task ShouldUseStaleFilmResponseWhenApiFails()
        {
            var cachePath = CreateTemporaryCachePath();

            try
            {
                var diagnostics = new KinopoiskDiagnostics();
                var options = CreateOptions(cachePath);
                options.MetadataExpiration = TimeSpan.FromMilliseconds(50);
                options.UseStaleCacheOnFailure = true;

                using (var firstMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var firstClient = CreateClient(
                        new FakeKinopoiskApiClient(),
                        firstMemoryCache,
                        options,
                        diagnostics);

                    await firstClient.GetSingleFilm(5273, CancellationToken.None);
                }

                await Task.Delay(TimeSpan.FromMilliseconds(150));

                var unavailableInnerClient = new FakeKinopoiskApiClient
                {
                    GetSingleFilmHandler = _ => throw new InvalidOperationException("API недоступен")
                };

                using (var secondMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var secondClient = CreateClient(
                        unavailableInnerClient,
                        secondMemoryCache,
                        options,
                        diagnostics);

                    var result = await secondClient.GetSingleFilm(5273, CancellationToken.None);

                    Assert.Equal(5273, result.KinopoiskId);
                    Assert.Equal("Шрэк 2", result.NameRu);
                }

                var snapshot = diagnostics.GetSnapshot();
                Assert.Equal(1, unavailableInnerClient.GetSingleFilmCalls);
                Assert.Equal(1, snapshot.ApiFailures);
                Assert.Equal(1, snapshot.StaleCacheHits);
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Fact]
        public async Task ShouldNotPersistTrailers()
        {
            var cachePath = CreateTemporaryCachePath();

            try
            {
                var diagnostics = new KinopoiskDiagnostics();
                var options = CreateOptions(cachePath);
                var firstInnerClient = new FakeKinopoiskApiClient();

                using (var firstMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var firstClient = CreateClient(
                        firstInnerClient,
                        firstMemoryCache,
                        options,
                        diagnostics);

                    await firstClient.GetTrailers(5273, CancellationToken.None);
                    await firstClient.GetTrailers(5273, CancellationToken.None);
                }

                var secondInnerClient = new FakeKinopoiskApiClient();

                using (var secondMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var secondClient = CreateClient(
                        secondInnerClient,
                        secondMemoryCache,
                        options,
                        diagnostics);

                    await secondClient.GetTrailers(5273, CancellationToken.None);
                }

                var snapshot = diagnostics.GetSnapshot();
                Assert.Equal(1, firstInnerClient.GetTrailersCalls);
                Assert.Equal(1, secondInnerClient.GetTrailersCalls);
                Assert.Equal(1, snapshot.MemoryCacheHits);
                Assert.Equal(0, snapshot.PersistentCacheHits);
                Assert.Equal(0, snapshot.PersistentCacheWrites);
                Assert.Empty(Directory.GetFiles(cachePath, "*.json", SearchOption.TopDirectoryOnly));
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        private static CachedKinopoiskApiClient CreateClient(
            IKinopoiskApiClient innerClient,
            IMemoryCache memoryCache,
            KinopoiskCacheOptions options,
            KinopoiskDiagnostics diagnostics)
        {
            return new CachedKinopoiskApiClient(
                innerClient,
                memoryCache,
                NullLogger<CachedKinopoiskApiClient>.Instance,
                options,
                diagnostics);
        }

        private static KinopoiskCacheOptions CreateOptions(string cachePath)
        {
            return new KinopoiskCacheOptions
            {
                EnablePersistentCache = true,
                UseStaleCacheOnFailure = true,
                PersistentCachePath = cachePath,
                MetadataExpiration = TimeSpan.FromHours(1),
                ImagesExpiration = TimeSpan.FromHours(1),
                SearchExpiration = TimeSpan.FromMinutes(15),
                EmptyResultExpiration = TimeSpan.FromMinutes(3),
                MaximumPersistentCacheBytes = 64L * 1024L * 1024L
            };
        }

        private static string CreateTemporaryCachePath()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-cache-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTemporaryCache(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
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

        private static VideoResponse CreateTrailers()
        {
            return new VideoResponse
            {
                Items = new List<VideoResponse_items>
                {
                    new VideoResponse_items
                    {
                        Name = "Официальный трейлер",
                        Site = VideoResponse_itemsSite.YOUTUBE,
                        Url = "https://www.youtube.com/watch?v=test"
                    }
                }
            };
        }

        private sealed class FakeKinopoiskApiClient : IKinopoiskApiClient
        {
            public Func<CancellationToken, Task<Film>> GetSingleFilmHandler { get; set; }

            public Func<CancellationToken, Task<VideoResponse>> GetTrailersHandler { get; set; }

            public int GetSingleFilmCalls { get; private set; }

            public int GetTrailersCalls { get; private set; }

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetSingleFilmCalls++;
                return GetSingleFilmHandler?.Invoke(cancellationToken ?? CancellationToken.None)
                    ?? Task.FromResult(CreateFilm());
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                GetTrailersCalls++;
                return GetTrailersHandler?.Invoke(cancellationToken ?? CancellationToken.None)
                    ?? Task.FromResult(CreateTrailers());
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
