using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class VideoImageProviderTests
    {
        [Fact]
        public async Task ShouldMapAllImageCategories()
        {
            var apiClient = new RecordingApiClient();
            var provider = CreateProvider(apiClient, new FixedResolver(true, 5273));

            var images = (await provider.GetImages(new Movie(), CancellationToken.None)).ToArray();

            Assert.Equal(9, apiClient.RequestedTypes.Count);
            Assert.Equal(2, images.Count(image => image.Type == ImageType.Primary));
            Assert.Equal(4, images.Count(image => image.Type == ImageType.Backdrop));
            Assert.Equal(3, images.Count(image => image.Type == ImageType.Screenshot));
            Assert.Equal(9, images.Select(image => image.Url).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        }

        [Fact]
        public async Task ShouldPreserveAvailableCategoriesWhenOneRequestFails()
        {
            var apiClient = new RecordingApiClient(FilmImageType.CONCEPT);
            var provider = CreateProvider(apiClient, new FixedResolver(true, 430));

            var images = (await provider.GetImages(new Movie(), CancellationToken.None)).ToArray();

            Assert.Equal(9, apiClient.RequestedTypes.Count);
            Assert.Equal(8, images.Length);
            Assert.DoesNotContain(images, image => image.Url.Contains("CONCEPT", StringComparison.Ordinal));
        }

        [Fact]
        public async Task ShouldSkipRequestsWhenProviderIdentifierIsMissing()
        {
            var apiClient = new RecordingApiClient();
            var provider = CreateProvider(apiClient, new FixedResolver(false, 0));

            var images = await provider.GetImages(new Movie(), CancellationToken.None);

            Assert.Empty(images);
            Assert.Empty(apiClient.RequestedTypes);
            Assert.Equal(0, apiClient.FilmRequestCount);
        }

        private static VideoImageProvider CreateProvider(
            RecordingApiClient apiClient,
            IProviderIdResolver<BaseItem> resolver)
        {
            return new VideoImageProvider(
                apiClient,
                resolver,
                NullLogger<VideoImageProvider>.Instance,
                new UnusedHttpClientFactory());
        }

        private sealed class FixedResolver : IProviderIdResolver<BaseItem>
        {
            private readonly bool _isSuccess;
            private readonly int _providerId;

            public FixedResolver(bool isSuccess, int providerId)
            {
                _isSuccess = isSuccess;
                _providerId = providerId;
            }

            public Task<(bool IsSuccess, int ProviderId)> TryResolve(
                BaseItem info,
                CancellationToken? ct = null)
            {
                return Task.FromResult((_isSuccess, _providerId));
            }
        }

        private sealed class RecordingApiClient : IKinopoiskApiClient, IKinopoiskImageApiClient
        {
            private readonly FilmImageType? _failedType;
            private int _filmRequestCount;

            public RecordingApiClient(FilmImageType? failedType = null)
            {
                _failedType = failedType;
            }

            public ConcurrentBag<FilmImageType> RequestedTypes { get; } = new();

            public int FilmRequestCount => Volatile.Read(ref _filmRequestCount);

            public Task<Film> GetSingleFilm(int filmId, CancellationToken? cancellationToken = null)
            {
                Interlocked.Increment(ref _filmRequestCount);
                return Task.FromResult(new Film
                {
                    KinopoiskId = filmId,
                    NameRu = "Тест",
                    Year = 2004,
                    Type = FilmType.FILM,
                    PosterUrl = null
                });
            }

            public Task<ImageResponse> GetImages(
                int filmId,
                FilmImageType type,
                int page = 1,
                CancellationToken? cancellationToken = null)
            {
                RequestedTypes.Add(type);
                if (_failedType == type)
                    throw new HttpRequestException("Временная ошибка тестовой категории изображений.");

                return Task.FromResult(new ImageResponse
                {
                    Total = 1,
                    TotalPages = 1,
                    Items = new[]
                    {
                        new ImageResponseItem
                        {
                            ImageUrl = $"https://example.org/{filmId}/{type}.jpg",
                            PreviewUrl = $"https://example.org/{filmId}/{type}-preview.jpg"
                        }
                    }
                });
            }

            public Task<PersonResponse> GetPerson(int personId, CancellationToken? cancellationToken = null)
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

        private sealed class UnusedHttpClientFactory : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                throw new InvalidOperationException("HTTP-клиент не должен использоваться в этом тесте.");
            }
        }
    }
}
