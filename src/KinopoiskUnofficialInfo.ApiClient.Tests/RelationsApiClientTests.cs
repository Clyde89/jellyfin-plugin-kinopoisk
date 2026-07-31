using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class RelationsApiClientTests
    {
        [Fact]
        public async Task ShouldRequestExpectedRelationsEndpoint()
        {
            var handler = new RecordingHandler(_ => JsonResponse(
                "[{\"filmId\":2,\"nameRu\":\"Сиквел\",\"nameEn\":\"Sequel\",\"posterUrl\":\"https://example.test/p.jpg\",\"posterUrlPreview\":\"https://example.test/pp.jpg\",\"relationType\":\"SEQUEL\"}]"));
            var client = CreateClient(handler);

            var result = await client.GetRelations(1);

            var relation = Assert.Single(result);
            Assert.Equal(2, relation.FilmId);
            Assert.Equal(FilmSequelsAndPrequelsResponseRelationType.SEQUEL, relation.RelationType);
            Assert.Equal(
                "https://kinopoiskapiunofficial.tech/api/v2.1/films/1/sequels_and_prequels",
                handler.LastRequestUri?.ToString());
            Assert.Equal("test-token", handler.LastApiKey);
        }

        [Fact]
        public async Task ShouldReturnEmptyCollectionForNotFoundResponse()
        {
            var handler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
            var client = CreateClient(handler);

            var result = await client.GetRelations(404);

            Assert.Empty(result);
            Assert.Equal(1, handler.RequestCount);
        }

        [Fact]
        public async Task ShouldRestoreRelationsFromPersistentCacheAfterMemoryCacheRecreation()
        {
            var cachePath = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-relations-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(cachePath);

            try
            {
                var inner = new CountingRelationsClient();
                var options = new KinopoiskCacheOptions
                {
                    EnablePersistentCache = true,
                    UseStaleCacheOnFailure = true,
                    PersistentCachePath = cachePath,
                    MetadataExpiration = TimeSpan.FromDays(30),
                    EmptyResultExpiration = TimeSpan.FromMinutes(15),
                    MaximumPersistentCacheBytes = 64 * 1024 * 1024
                };

                using (var firstMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var firstClient = new CachedKinopoiskRelationsApiClient(
                        inner,
                        firstMemoryCache,
                        options,
                        new KinopoiskDiagnostics(),
                        NullLogger<CachedKinopoiskRelationsApiClient>.Instance);
                    var firstResult = await firstClient.GetRelations(10);
                    Assert.Single(firstResult);
                }

                using (var secondMemoryCache = new MemoryCache(new MemoryCacheOptions()))
                {
                    var secondClient = new CachedKinopoiskRelationsApiClient(
                        inner,
                        secondMemoryCache,
                        options,
                        new KinopoiskDiagnostics(),
                        NullLogger<CachedKinopoiskRelationsApiClient>.Instance);
                    var secondResult = await secondClient.GetRelations(10);
                    Assert.Single(secondResult);
                }

                Assert.Equal(1, inner.RequestCount);
            }
            finally
            {
                Directory.Delete(cachePath, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldCollapseConcurrentRequestsIntoSinglePhysicalCall()
        {
            var inner = new CountingRelationsClient(delayMilliseconds: 50);
            using var memoryCache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskRelationsApiClient(
                inner,
                memoryCache,
                new KinopoiskCacheOptions { EnablePersistentCache = false },
                new KinopoiskDiagnostics(),
                NullLogger<CachedKinopoiskRelationsApiClient>.Instance);

            var results = await Task.WhenAll(
                client.GetRelations(20),
                client.GetRelations(20),
                client.GetRelations(20));

            Assert.All(results, result => Assert.Single(result));
            Assert.Equal(1, inner.RequestCount);
        }

        private static KinopoiskRelationsApiClient CreateClient(HttpMessageHandler handler)
        {
            return new KinopoiskRelationsApiClient(
                "test-token",
                new SingleHttpClientFactory(new HttpClient(handler)),
                NullLogger<KinopoiskRelationsApiClient>.Instance);
        }

        private static HttpResponseMessage JsonResponse(string json)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
        }

        private sealed class SingleHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _httpClient;

            public SingleHttpClientFactory(HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public HttpClient CreateClient(string name) => _httpClient;
        }

        private sealed class RecordingHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _responseFactory;

            public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
            {
                _responseFactory = responseFactory;
            }

            public int RequestCount { get; private set; }

            public Uri LastRequestUri { get; private set; }

            public string LastApiKey { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                LastRequestUri = request.RequestUri;
                LastApiKey = request.Headers.TryGetValues("X-API-KEY", out var values)
                    ? string.Join(",", values)
                    : string.Empty;
                return Task.FromResult(_responseFactory(request));
            }
        }

        private sealed class CountingRelationsClient : IKinopoiskRelationsApiClient
        {
            private readonly int _delayMilliseconds;

            public CountingRelationsClient(int delayMilliseconds = 0)
            {
                _delayMilliseconds = delayMilliseconds;
            }

            public int RequestCount { get; private set; }

            public async Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelations(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RequestCount++;
                if (_delayMilliseconds > 0)
                    await Task.Delay(_delayMilliseconds, cancellationToken ?? CancellationToken.None);

                return new[]
                {
                    new FilmSequelsAndPrequelsResponse
                    {
                        FilmId = filmId + 1,
                        NameRu = "Связанный фильм",
                        NameEn = "Related film",
                        PosterUrl = "https://example.test/poster.jpg",
                        PosterUrlPreview = "https://example.test/poster-small.jpg",
                        RelationType = FilmSequelsAndPrequelsResponseRelationType.SEQUEL
                    }
                };
            }
        }
    }
}
