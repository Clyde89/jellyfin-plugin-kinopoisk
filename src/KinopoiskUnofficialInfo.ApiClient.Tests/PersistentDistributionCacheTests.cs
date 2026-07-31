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
    public class PersistentDistributionCacheTests
    {
        [Fact]
        public async Task ShouldReusePersistentDistributionsAfterMemoryCacheRecreation()
        {
            var cachePath = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-distribution-cache-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cachePath);

            try
            {
                var diagnostics = new KinopoiskDiagnostics();
                var options = new KinopoiskCacheOptions
                {
                    EnablePersistentCache = true,
                    UseStaleCacheOnFailure = true,
                    PersistentCachePath = cachePath,
                    MetadataExpiration = TimeSpan.FromHours(1),
                    MaximumPersistentCacheBytes = 64L * 1024L * 1024L
                };
                var firstInnerClient = new FakeDistributionApiClient();

                using (var memoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var client = CreateClient(firstInnerClient, memoryCache, options, diagnostics);
                    var result = await client.GetDistributions(5273, CancellationToken.None);
                    Assert.Single(result.Items);
                }

                var secondInnerClient = new FakeDistributionApiClient
                {
                    DistributionHandler = _ => throw new InvalidOperationException(
                        "API не должен вызываться при наличии дискового кэша.")
                };

                using (var memoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var client = CreateClient(secondInnerClient, memoryCache, options, diagnostics);
                    var result = await client.GetDistributions(5273, CancellationToken.None);
                    var distribution = Assert.Single(result.Items);
                    Assert.Equal("2004-05-19", distribution.Date);
                }

                Assert.Equal(1, firstInnerClient.DistributionCalls);
                Assert.Equal(0, secondInnerClient.DistributionCalls);
                Assert.Equal(1, diagnostics.GetSnapshot().PersistentCacheHits);
            }
            finally
            {
                if (Directory.Exists(cachePath))
                    Directory.Delete(cachePath, recursive: true);
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

        private sealed class FakeDistributionApiClient : IKinopoiskApiClient, IKinopoiskDistributionApiClient
        {
            public Func<CancellationToken, Task<DistributionResponse>> DistributionHandler { get; set; }

            public int DistributionCalls { get; private set; }

            public Task<DistributionResponse> GetDistributions(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                DistributionCalls++;
                return DistributionHandler?.Invoke(cancellationToken ?? CancellationToken.None)
                    ?? Task.FromResult(new DistributionResponse
                    {
                        Items = new List<Distribution>
                        {
                            new Distribution
                            {
                                Type = DistributionType.WORLD_PREMIER,
                                SubType = DistributionSubType.CINEMA,
                                Date = "2004-05-19"
                            }
                        }
                    });
            }

            public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<ICollection<StaffResponse>> GetStaff(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<FilmSearchResponse> SearchByKeyword(string keyword, int page = 1, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();

            public Task<VideoResponse> GetTrailers(int filmId, CancellationToken? cancellationToken = null)
                => throw new NotSupportedException();
        }
    }
}
