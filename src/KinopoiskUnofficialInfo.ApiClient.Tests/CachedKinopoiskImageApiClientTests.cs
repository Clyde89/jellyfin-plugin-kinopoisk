using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace KinopoiskUnofficialInfo.ApiClient.Tests
{
    public class CachedKinopoiskImageApiClientTests
    {
        [Fact]
        public async Task ShouldCacheImagesByFilmTypeAndPage()
        {
            var innerClient = new RecordingImageApiClient();
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var first = await client.GetImages(5273, FilmImageType.POSTER, 1, CancellationToken.None);
            var second = await client.GetImages(5273, FilmImageType.POSTER, 1, CancellationToken.None);
            await client.GetImages(5273, FilmImageType.FAN_ART, 1, CancellationToken.None);
            await client.GetImages(5273, FilmImageType.POSTER, 2, CancellationToken.None);

            Assert.Same(first, second);
            Assert.Equal(3, innerClient.ImageRequestCount);
        }

        [Fact]
        public async Task ShouldCombineConcurrentImageRequests()
        {
            var innerClient = new RecordingImageApiClient(delay: TimeSpan.FromMilliseconds(50));
            using var cache = new MemoryCache(new MemoryCacheOptions());
            var client = new CachedKinopoiskApiClient(
                innerClient,
                cache,
                NullLogger<CachedKinopoiskApiClient>.Instance);

            var requests = new Task<ImageResponse>[8];
            for (var index = 0; index < requests.Length; index++)
            {
                requests[index] = client.GetImages(
                    430,
                    FilmImageType.STILL,
                    1,
                    CancellationToken.None);
            }

            await Task.WhenAll(requests);

            Assert.Equal(1, innerClient.ImageRequestCount);
            foreach (var request in requests)
                Assert.Same(requests[0].Result, request.Result);
        }

        private sealed class RecordingImageApiClient : IKinopoiskApiClient, IKinopoiskImageApiClient
        {
            private readonly TimeSpan _delay;
            private int _imageRequestCount;

            public RecordingImageApiClient(TimeSpan? delay = null)
            {
                _delay = delay ?? TimeSpan.Zero;
            }

            public int ImageRequestCount => Volatile.Read(ref _imageRequestCount);

            public async Task<ImageResponse> GetImages(
                int filmId,
                FilmImageType type,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                Interlocked.Increment(ref _imageRequestCount);
                if (_delay > TimeSpan.Zero)
                    await Task.Delay(_delay, cancellationToken ?? CancellationToken.None);

                return new ImageResponse
                {
                    Total = 1,
                    TotalPages = 1,
                    Items = new[]
                    {
                        new ImageResponseItem
                        {
                            ImageUrl = $"https://example.org/{filmId}/{type}/{page}.jpg",
                            PreviewUrl = $"https://example.org/{filmId}/{type}/{page}-preview.jpg"
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
