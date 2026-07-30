using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class RateLimitedKinopoiskApiClientTests
    {
        [Fact]
        public async Task ShouldSpaceConcurrentRequestStarts()
        {
            var innerClient = new RecordingKinopoiskApiClient();
            var client = new RateLimitedKinopoiskApiClient(innerClient);

            await Task.WhenAll(
                client.SearchFilms(CreateQuery("Первый"), CancellationToken.None),
                client.SearchFilms(CreateQuery("Второй"), CancellationToken.None),
                client.SearchFilms(CreateQuery("Третий"), CancellationToken.None));

            var timestamps = innerClient.RequestTimestamps
                .OrderBy(value => value)
                .ToArray();

            Assert.Equal(3, timestamps.Length);

            for (var index = 1; index < timestamps.Length; index++)
            {
                var elapsed = TimeSpan.FromSeconds(
                    (timestamps[index] - timestamps[index - 1])
                    / (double)Stopwatch.Frequency);

                Assert.True(
                    elapsed >= TimeSpan.FromMilliseconds(150),
                    $"Интервал между запросами составил {elapsed.TotalMilliseconds:F0} мс.");
            }
        }

        [Fact]
        public async Task ShouldCancelRequestWhileWaitingForRateLimit()
        {
            var innerClient = new RecordingKinopoiskApiClient();
            var client = new RateLimitedKinopoiskApiClient(innerClient);

            await client.GetSingleFilm(5273, CancellationToken.None);

            using var cancellationTokenSource = new CancellationTokenSource(
                TimeSpan.FromMilliseconds(30));

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                client.GetSingleFilm(430, cancellationTokenSource.Token));

            Assert.Equal(1, innerClient.RequestTimestamps.Count);
        }

        [Fact]
        public async Task ShouldRejectFilteredSearchWhenInnerClientDoesNotSupportIt()
        {
            var client = new RateLimitedKinopoiskApiClient(
                new BasicKinopoiskApiClient());

            await Assert.ThrowsAsync<NotSupportedException>(() =>
                client.SearchFilms(CreateQuery("Тест"), CancellationToken.None));
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

        private sealed class RecordingKinopoiskApiClient : IFilteredKinopoiskApiClient
        {
            public ConcurrentQueue<long> RequestTimestamps { get; } = new();

            public Task<FilteredFilmSearchResponse> SearchFilms(
                FilmSearchQuery query,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult(new FilteredFilmSearchResponse
                {
                    Total = 0,
                    TotalPages = 0,
                    Items = Array.Empty<FilteredFilmSearchItem>()
                });
            }

            public Task<Film> GetSingleFilm(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult(new Film
                {
                    KinopoiskId = filmId,
                    NameRu = "Тест",
                    Year = 2026,
                    Type = FilmType.FILM
                });
            }

            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult(new PersonResponse());
            }

            public Task<ICollection<StaffResponse>> GetStaff(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult<ICollection<StaffResponse>>(
                    Array.Empty<StaffResponse>());
            }

            public Task<VideoResponse> GetTrailers(
                int filmId,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult(new VideoResponse());
            }

            public Task<FilmSearchResponse> SearchByKeyword(
                string keyword,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                RecordRequest();
                return Task.FromResult(new FilmSearchResponse());
            }

            private void RecordRequest()
            {
                RequestTimestamps.Enqueue(Stopwatch.GetTimestamp());
            }
        }

        private sealed class BasicKinopoiskApiClient : IKinopoiskApiClient
        {
            public Task<PersonResponse> GetPerson(
                int personId,
                CancellationToken? cancellationToken = null)
            {
                throw new NotSupportedException();
            }

            public Task<Film> GetSingleFilm(
                int filmId,
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
