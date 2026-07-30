using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientRateLimitTests
    {
        private const string EmptySearchResponse = "{\"total\":0,\"totalPages\":0,\"items\":[]}";

        [Fact]
        public async Task ShouldSpaceConcurrentPhysicalRequests()
        {
            var handler = new RecordingSequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, EmptySearchResponse)));
            var client = CreateClient(handler);

            await Task.WhenAll(
                client.SearchFilms(CreateQuery("Первый"), CancellationToken.None),
                client.SearchFilms(CreateQuery("Второй"), CancellationToken.None),
                client.SearchFilms(CreateQuery("Третий"), CancellationToken.None));

            AssertRequestIntervals(handler.RequestTimestamps.ToArray(), 3);
            Assert.Equal(3, handler.CallCount);
        }

        [Fact]
        public async Task ShouldSpaceRetryAttemptsThroughSameLimit()
        {
            var handler = new RecordingSequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.ServiceUnavailable, "{}", "0")),
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, EmptySearchResponse)));
            var client = CreateClient(handler);

            var result = await client.SearchFilms(CreateQuery("Повтор"), CancellationToken.None);

            Assert.NotNull(result);
            AssertRequestIntervals(handler.RequestTimestamps.ToArray(), 2);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task ShouldCancelWhileWaitingForPhysicalRequestWindow()
        {
            var handler = new RecordingSequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, EmptySearchResponse)));
            var client = CreateClient(handler);

            await client.SearchFilms(CreateQuery("Первый"), CancellationToken.None);

            using var cancellationTokenSource = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(30));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.SearchFilms(CreateQuery("Отменённый"), cancellationTokenSource.Token));

            Assert.Equal(1, handler.CallCount);
            Assert.Single(handler.RequestTimestamps);
        }

        private static void AssertRequestIntervals(long[] timestamps, int expectedCount)
        {
            Assert.Equal(expectedCount, timestamps.Length);

            for (var index = 1; index < timestamps.Length; index++)
            {
                var elapsed = TimeSpan.FromSeconds(
                    (timestamps[index] - timestamps[index - 1])
                    / (double)Stopwatch.Frequency);

                Assert.True(
                    elapsed >= TimeSpan.FromMilliseconds(150),
                    $"Интервал между физическими запросами составил {elapsed.TotalMilliseconds:F0} мс.");
            }
        }

        private static FilmSearchQuery CreateQuery(string keyword)
        {
            return new FilmSearchQuery
            {
                Keyword = keyword,
                Type = "FILM",
                Page = 1
            };
        }

        private static KinopoiskApiClient CreateClient(HttpMessageHandler handler)
        {
            return new KinopoiskApiClient(
                "test-token",
                NullLogger<KinopoiskApiClient>.Instance,
                new FixedHttpClientFactory(new HttpClient(handler)));
        }

        private static HttpResponseMessage CreateResponse(
            HttpStatusCode statusCode,
            string content,
            string retryAfter = null)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = new StringContent(content)
            };

            if (!string.IsNullOrWhiteSpace(retryAfter))
                response.Headers.TryAddWithoutValidation("Retry-After", retryAfter);

            return response;
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

        private sealed class RecordingSequenceHandler : HttpMessageHandler
        {
            private readonly IReadOnlyList<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps;
            private int _callCount;

            public RecordingSequenceHandler(
                params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
            {
                _steps = steps ?? throw new ArgumentNullException(nameof(steps));
                if (_steps.Count < 1)
                    throw new ArgumentException("Последовательность ответов не должна быть пустой.", nameof(steps));
            }

            public ConcurrentQueue<long> RequestTimestamps { get; } = new();

            public int CallCount => Volatile.Read(ref _callCount);

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                RequestTimestamps.Enqueue(Stopwatch.GetTimestamp());
                var callNumber = Interlocked.Increment(ref _callCount);
                var index = Math.Min(callNumber - 1, _steps.Count - 1);
                return _steps[index](request, cancellationToken);
            }
        }
    }
}
