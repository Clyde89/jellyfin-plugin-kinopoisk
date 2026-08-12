using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTrailerStreamResolverTests
    {
        [Fact]
        public async Task ShouldResolveAndCacheVerifiedHlsManifest()
        {
            var requests = new List<HttpRequestMessage>();
            using var client = new HttpClient(new DelegateHandler(request =>
            {
                requests.Add(CloneRequest(request));
                if (request.RequestUri!.Host == "widgets.kinopoisk.ru")
                {
                    return new HttpResponseMessage(HttpStatusCode.OK)
                    {
                        Content = new StringContent(
                            "{\"src\":\"https:\\/\\/strm.yandex.ru\\/trailer\\/master.m3u8?token=1\"}")
                    };
                }

                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent("#EXTM3U\n#EXT-X-VERSION:3\n")
                };
            }));
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var resolver = new KinopoiskTrailerStreamResolver(
                new FixedHttpClientFactory(client),
                cache,
                NullLogger<KinopoiskTrailerStreamResolver>.Instance);

            var first = await resolver.Resolve(
                "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1",
                CancellationToken.None);
            var second = await resolver.Resolve(
                "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1",
                CancellationToken.None);

            Assert.NotNull(first);
            Assert.Same(first, second);
            Assert.Equal(
                "https://strm.yandex.ru/trailer/master.m3u8?token=1",
                first!.MediaUrl.AbsoluteUri);
            Assert.Equal(2, requests.Count);
            Assert.Equal(
                "https://widgets.kinopoisk.ru",
                requests[1].Headers.GetValues("Origin").Single());
            Assert.Equal(
                "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1",
                requests[1].Headers.Referrer!.AbsoluteUri);
        }

        [Fact]
        public async Task ShouldRejectRedirectOutsideTrustedWidgetHost()
        {
            var calls = 0;
            using var client = new HttpClient(new DelegateHandler(_ =>
            {
                calls++;
                var response = new HttpResponseMessage(HttpStatusCode.Redirect);
                response.Headers.Location = new Uri("https://127.0.0.1/private");
                return response;
            }));
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var resolver = new KinopoiskTrailerStreamResolver(
                new FixedHttpClientFactory(client),
                cache,
                NullLogger<KinopoiskTrailerStreamResolver>.Instance);

            await Assert.ThrowsAsync<HttpRequestException>(() => resolver.Resolve(
                "https://widgets.kinopoisk.ru/discovery/film/430/trailer/1",
                CancellationToken.None));

            Assert.Equal(1, calls);
        }

        private static HttpRequestMessage CloneRequest(HttpRequestMessage source)
        {
            var clone = new HttpRequestMessage(source.Method, source.RequestUri);
            foreach (var header in source.Headers)
                clone.Headers.TryAddWithoutValidation(header.Key, header.Value);
            return clone;
        }

        private sealed class DelegateHandler : HttpMessageHandler
        {
            private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

            public DelegateHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
            {
                _handler = handler;
            }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
                => Task.FromResult(_handler(request));
        }

        private sealed class FixedHttpClientFactory : IHttpClientFactory
        {
            private readonly HttpClient _client;

            public FixedHttpClientFactory(HttpClient client)
            {
                _client = client;
            }

            public HttpClient CreateClient(string name) => _client;
        }
    }
}
