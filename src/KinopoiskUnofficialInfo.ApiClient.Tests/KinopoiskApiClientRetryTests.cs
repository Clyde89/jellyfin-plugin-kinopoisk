using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class KinopoiskApiClientRetryTests
    {
        private const string EmptySearchResponse = "{\"total\":0,\"totalPages\":0,\"items\":[]}";

        [Fact]
        public async Task ShouldRetryRateLimitAndReturnSuccessfulResponse()
        {
            var handler = new SequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.TooManyRequests, "{}", "0")),
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.TooManyRequests, "{}", "0")),
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, EmptySearchResponse)));
            var client = CreateClient(handler);

            var result = await client.SearchFilms(CreateQuery(), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(3, handler.CallCount);
        }

        [Fact]
        public async Task ShouldStopAfterMaximumTransientAttempts()
        {
            var handler = new SequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.ServiceUnavailable, "{}", "0")));
            var client = CreateClient(handler);

            var exception = await Assert.ThrowsAsync<ApiException>(() =>
                client.SearchFilms(CreateQuery(), CancellationToken.None));

            Assert.Equal((int)HttpStatusCode.ServiceUnavailable, exception.StatusCode);
            Assert.Equal(3, handler.CallCount);
        }

        [Fact]
        public async Task ShouldNotRetryUnauthorizedResponse()
        {
            var handler = new SequenceHandler(
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.Unauthorized, "{}")));
            var client = CreateClient(handler);

            var exception = await Assert.ThrowsAsync<ApiException>(() =>
                client.SearchFilms(CreateQuery(), CancellationToken.None));

            Assert.Equal((int)HttpStatusCode.Unauthorized, exception.StatusCode);
            Assert.Equal(1, handler.CallCount);
        }

        [Fact]
        public async Task ShouldRetryTemporaryNetworkFailure()
        {
            var handler = new SequenceHandler(
                (_, _) => Task.FromException<HttpResponseMessage>(
                    new HttpRequestException("Временная сетевая ошибка")),
                (_, _) => Task.FromResult(CreateResponse(HttpStatusCode.OK, EmptySearchResponse)));
            var client = CreateClient(handler);

            var result = await client.SearchFilms(CreateQuery(), CancellationToken.None);

            Assert.NotNull(result);
            Assert.Equal(2, handler.CallCount);
        }

        [Fact]
        public async Task ShouldPropagateCallerCancellationWithoutRetry()
        {
            using var cancellationTokenSource = new CancellationTokenSource();
            var handler = new SequenceHandler(
                (_, _) =>
                {
                    cancellationTokenSource.Cancel();
                    return Task.FromException<HttpResponseMessage>(
                        new OperationCanceledException(cancellationTokenSource.Token));
                });
            var client = CreateClient(handler);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.SearchFilms(CreateQuery(), cancellationTokenSource.Token));

            Assert.Equal(1, handler.CallCount);
        }

        private static FilmSearchQuery CreateQuery()
        {
            return new FilmSearchQuery
            {
                Keyword = "Shrek 2",
                YearFrom = 2004,
                YearTo = 2004,
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

        private sealed class SequenceHandler : HttpMessageHandler
        {
            private readonly IReadOnlyList<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> _steps;

            public SequenceHandler(
                params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
            {
                _steps = steps ?? throw new ArgumentNullException(nameof(steps));
                if (_steps.Count < 1)
                    throw new ArgumentException("Последовательность ответов не должна быть пустой.", nameof(steps));
            }

            public int CallCount { get; private set; }

            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request,
                CancellationToken cancellationToken)
            {
                var index = Math.Min(CallCount, _steps.Count - 1);
                CallCount++;
                return _steps[index](request, cancellationToken);
            }
        }
    }
}
