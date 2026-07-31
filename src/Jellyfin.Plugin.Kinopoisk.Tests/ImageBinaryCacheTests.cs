using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class ImageBinaryCacheTests
    {
        private const string ImageUrl = "https://images.example.test/poster.jpg";

        [Fact]
        public async Task ShouldReuseFreshImageWithoutSecondHttpRequest()
        {
            var cachePath = CreateTemporaryCachePath();
            try
            {
                var imageBytes = new byte[] { 1, 2, 3, 4, 5 };
                var handler = new SequenceHttpMessageHandler(_ => CreateImageResponse(imageBytes, "\"v1\""));
                var diagnostics = new KinopoiskDiagnostics();
                var cache = CreateCache(cachePath, handler, diagnostics, TimeSpan.FromDays(30));

                using var firstResponse = await cache.GetImageResponse(ImageUrl, CancellationToken.None);
                using var secondResponse = await cache.GetImageResponse(ImageUrl, CancellationToken.None);

                Assert.Equal(imageBytes, await firstResponse.Content.ReadAsByteArrayAsync());
                Assert.Equal(imageBytes, await secondResponse.Content.ReadAsByteArrayAsync());
                Assert.Equal(1, handler.RequestCount);
                Assert.Equal("HIT", secondResponse.Headers.GetValues("X-Kinopoisk-Image-Cache").Single());

                var snapshot = diagnostics.GetSnapshot();
                Assert.Equal(1, snapshot.ImageCacheMisses);
                Assert.Equal(1, snapshot.ImageCacheHits);
                Assert.Equal(1, snapshot.ImageCacheWrites);
                Assert.Equal(imageBytes.Length, snapshot.ImageCacheBytesWritten);
                Assert.Single(Directory.GetFiles(cachePath, "*.img"));
                Assert.Single(Directory.GetFiles(cachePath, "*.json"));
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Fact]
        public async Task ShouldRevalidateExpiredImageWithEntityTag()
        {
            var cachePath = CreateTemporaryCachePath();
            try
            {
                var imageBytes = new byte[] { 10, 20, 30 };
                var handler = new SequenceHttpMessageHandler(request =>
                {
                    if (request.Headers.IfNoneMatch.Any(tag => tag.Tag == "\"v1\""))
                        return new HttpResponseMessage(HttpStatusCode.NotModified);

                    return CreateImageResponse(imageBytes, "\"v1\"");
                });
                var diagnostics = new KinopoiskDiagnostics();
                var cache = CreateCache(
                    cachePath,
                    handler,
                    diagnostics,
                    TimeSpan.FromMilliseconds(50));

                using (var response = await cache.GetImageResponse(ImageUrl, CancellationToken.None))
                    Assert.Equal(imageBytes, await response.Content.ReadAsByteArrayAsync());

                await Task.Delay(150);

                using (var response = await cache.GetImageResponse(ImageUrl, CancellationToken.None))
                    Assert.Equal(imageBytes, await response.Content.ReadAsByteArrayAsync());

                Assert.Equal(2, handler.RequestCount);
                Assert.True(handler.LastRequestHadConditionalHeader);
                Assert.Equal(1, diagnostics.GetSnapshot().ImageCacheWrites);
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Fact]
        public async Task ShouldUseStaleImageWhenRemoteServerFails()
        {
            var cachePath = CreateTemporaryCachePath();
            try
            {
                var imageBytes = new byte[] { 7, 8, 9 };
                var handler = new SequenceHttpMessageHandler(
                    request => request.Headers.IfNoneMatch.Count > 0
                        ? throw new HttpRequestException("Удалённый сервер недоступен.")
                        : CreateImageResponse(imageBytes, "\"v1\""));
                var diagnostics = new KinopoiskDiagnostics();
                var cache = CreateCache(
                    cachePath,
                    handler,
                    diagnostics,
                    TimeSpan.FromMilliseconds(50),
                    useStaleOnFailure: true);

                using (var response = await cache.GetImageResponse(ImageUrl, CancellationToken.None))
                    Assert.Equal(imageBytes, await response.Content.ReadAsByteArrayAsync());

                await Task.Delay(150);

                using (var response = await cache.GetImageResponse(ImageUrl, CancellationToken.None))
                    Assert.Equal(imageBytes, await response.Content.ReadAsByteArrayAsync());

                Assert.Equal(2, handler.RequestCount);
                Assert.Equal(1, diagnostics.GetSnapshot().ImageCacheStaleHits);
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Fact]
        public async Task ShouldNotPersistImageLargerThanConfiguredLimit()
        {
            var cachePath = CreateTemporaryCachePath();
            try
            {
                var oversizedImage = new byte[(1024 * 1024) + 1];
                var handler = new SequenceHttpMessageHandler(_ => CreateImageResponse(oversizedImage, null));
                var diagnostics = new KinopoiskDiagnostics();
                var cache = CreateCache(
                    cachePath,
                    handler,
                    diagnostics,
                    TimeSpan.FromDays(30),
                    maximumFileBytes: 1024 * 1024);

                using var response = await cache.GetImageResponse(ImageUrl, CancellationToken.None);
                Assert.Equal(oversizedImage.Length, (await response.Content.ReadAsByteArrayAsync()).Length);

                Assert.True(handler.RequestCount >= 2);
                Assert.Empty(Directory.GetFiles(cachePath, "*.img"));
                Assert.Empty(Directory.GetFiles(cachePath, "*.json"));
                Assert.Equal(0, diagnostics.GetSnapshot().ImageCacheWrites);
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        [Theory]
        [InlineData("")]
        [InlineData("file:///tmp/poster.jpg")]
        [InlineData("javascript:alert(1)")]
        public async Task ShouldRejectUnsupportedImageUrls(string url)
        {
            var cachePath = CreateTemporaryCachePath();
            try
            {
                var cache = CreateCache(
                    cachePath,
                    new SequenceHttpMessageHandler(_ => CreateImageResponse(new byte[] { 1 }, null)),
                    new KinopoiskDiagnostics(),
                    TimeSpan.FromDays(30));

                await Assert.ThrowsAsync<ArgumentException>(
                    () => cache.GetImageResponse(url, CancellationToken.None));
            }
            finally
            {
                DeleteTemporaryCache(cachePath);
            }
        }

        private static KinopoiskImageBinaryCache CreateCache(
            string cachePath,
            HttpMessageHandler handler,
            KinopoiskDiagnostics diagnostics,
            TimeSpan expiration,
            bool useStaleOnFailure = true,
            long maximumFileBytes = 25L * 1024L * 1024L)
        {
            return new KinopoiskImageBinaryCache(
                new FixedHttpClientFactory(new HttpClient(handler)),
                new KinopoiskImageCacheOptions
                {
                    Enabled = true,
                    UseStaleOnFailure = useStaleOnFailure,
                    CachePath = cachePath,
                    Expiration = expiration,
                    MaximumCacheBytes = 64L * 1024L * 1024L,
                    MaximumFileBytes = maximumFileBytes
                },
                diagnostics,
                NullLogger<KinopoiskImageBinaryCache>.Instance);
        }

        private static HttpResponseMessage CreateImageResponse(byte[] bytes, string entityTag)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(bytes)
            };
            response.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
            response.Content.Headers.ContentLength = bytes.Length;
            response.Content.Headers.LastModified = new DateTimeOffset(2026, 7, 31, 0, 0, 0, TimeSpan.Zero);

            if (!string.IsNullOrWhiteSpace(entityTag))
                response.Headers.ETag = new EntityTagHeaderValue(entityTag);

            return response;
        }

        private static string CreateTemporaryCachePath()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-image-cache-tests-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void DeleteTemporaryCache(string path)
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }

        private sealed class FixedHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _httpClient;

            public FixedHttpClientFactory(HttpClient httpClient)
            {
                _httpClient = httpClient;
            }

            public HttpClient CreateClient(string name)
            {
                return _httpClient;
            }
        }

        private sealed class SequenceHttpMessageHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public SequenceHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            public int RequestCount { get; private set; }

            public bool LastRequestHadConditionalHeader { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestCount++;
                LastRequestHadConditionalHeader = request.Headers.IfNoneMatch.Count > 0
                    || request.Headers.IfModifiedSince.HasValue;
                return Task.FromResult(_handler(request));
            }
        }
    }
}
