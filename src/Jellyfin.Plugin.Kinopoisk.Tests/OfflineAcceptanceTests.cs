using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using Jellyfin.Plugin.Kinopoisk.ProviderIdResolvers;
using Jellyfin.Plugin.Kinopoisk.Services;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Controller.Providers;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    [Collection("Запуск без токена")]
    public class OfflineAcceptanceTests
    {
        private const string ApiRoot = "https://kinopoiskapiunofficial.tech";
        private const string WidgetUrl = "https://widgets.kinopoisk.ru/discovery/film/5273/trailer/1";
        private const string ManifestUrl = "https://strm.yandex.ru/trailer/master.m3u8";
        private const string FilmJson = """
            {"kinopoiskId":5273,"imdbId":"tt0298148","nameRu":"Шрэк 2",
             "nameOriginal":"Shrek 2","year":2004,"type":"FILM","ratingKinopoisk":8.1,
             "description":"Продолжение приключений Шрэка.","filmLength":93,
             "isTicketsAvailable":false,"hasImax":false,"has3D":false,
             "countries":[{"country":"США"}],"genres":[{"genre":"мультфильм"}]}
            """;
        private const string DistributionsJson = """
            {"total":1,"items":[{"type":"WORLD_PREMIERE","date":"2004-05-19",
             "country":{"country":"США"},"companies":[]}]}
            """;
        private const string SeasonsJson = """
            {"total":1,"items":[{"number":1,"episodes":[
             {"seasonNumber":1,"episodeNumber":1,"nameRu":"Пилот","nameEn":"Pilot",
              "synopsis":"Описание первой серии","releaseDate":"2019-05-02"},
             {"seasonNumber":1,"episodeNumber":2,"nameRu":"Продолжение","nameEn":"Next",
              "synopsis":"Описание второй серии","releaseDate":"2019-05-09"}]}]}
            """;
        private const string VideosJson = """
            {"total":2,"items":[
             {"name":"Трейлер","site":"KINOPOISK_WIDGET","url":"https://widgets.kinopoisk.ru/discovery/film/5273/trailer/1"},
             {"name":"Трейлер","site":"YOUTUBE","url":"https://www.youtube.com/watch?v=YOUTUBE001"}]}
            """;

        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public async Task ShouldRestoreMovieMetadataAfterClientRecreationWithoutToken(bool expired)
        {
            var path = Path.Combine(Path.GetTempPath(), "kinopoisk-offline-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new KinopoiskCacheOptions
                {
                    EnablePersistentCache = true,
                    UseStaleCacheOnFailure = true,
                    PersistentCachePath = path,
                    MetadataExpiration = expired ? TimeSpan.FromTicks(1) : TimeSpan.FromHours(1)
                };
                using (var recorded = CreateMovieFixture())
                using (var memory = new MemoryCache(new MemoryCacheOptions()))
                {
                    var client = CreateClient(recorded, memory, "offline-fixture-token", options);
                    AssertMovie(await CreateMovieProvider(client, recorded).GetMetadata(CreateMovieInfo(), CancellationToken.None));
                    Assert.Equal(4, recorded.Requests.Count);
                }

                using var disconnected = new FixtureHttpFactory();
                using var recreatedMemory = new MemoryCache(new MemoryCacheOptions());
                var diagnostics = new KinopoiskDiagnostics();
                var recreated = CreateClient(disconnected, recreatedMemory, "", options, diagnostics);

                AssertMovie(await CreateMovieProvider(recreated, disconnected).GetMetadata(CreateMovieInfo(), CancellationToken.None));
                Assert.Empty(disconnected.Requests);
                var snapshot = diagnostics.GetSnapshot();
                if (expired)
                    Assert.Equal(2, snapshot.StaleCacheHits);
                else
                    Assert.Equal(3, snapshot.PersistentCacheHits);
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
        }

        [Fact]
        public async Task ShouldRestoreSeasonAndMultipartEpisodeFromPersistentResponse()
        {
            var path = Path.Combine(Path.GetTempPath(), "kinopoisk-offline-" + Guid.NewGuid().ToString("N"));
            try
            {
                var options = new KinopoiskCacheOptions { EnablePersistentCache = true, PersistentCachePath = path };
                var seasonInfo = new SeasonInfo { IndexNumber = 1 };
                seasonInfo.SeriesProviderIds[Constants.ProviderId] = "1046206";
                var episodeInfo = new EpisodeInfo { ParentIndexNumber = 1, IndexNumber = 1, IndexNumberEnd = 2 };
                episodeInfo.SeriesProviderIds[Constants.ProviderId] = "1046206";
                using (var recorded = new FixtureHttpFactory())
                using (var memory = new MemoryCache(new MemoryCacheOptions()))
                {
                    recorded.Add(ApiRoot + "/api/v2.2/films/1046206/seasons", SeasonsJson);
                    var client = CreateClient(recorded, memory, "offline-fixture-token", options);
                    var season = await new SeasonMetadataProvider(client, NullLogger<SeasonMetadataProvider>.Instance, recorded)
                        .GetMetadata(seasonInfo, CancellationToken.None);
                    var episode = await new EpisodeMetadataProvider(client, NullLogger<EpisodeMetadataProvider>.Instance, recorded)
                        .GetMetadata(episodeInfo, CancellationToken.None);
                    Assert.True(season.HasMetadata);
                    Assert.Equal("Сезон 1", season.Item.Name);
                    Assert.Equal("Пилот / Продолжение", episode.Item.Name);
                    Assert.Single(recorded.Requests);
                }

                using var disconnected = new FixtureHttpFactory();
                using var recreatedMemory = new MemoryCache(new MemoryCacheOptions());
                var recreated = CreateClient(disconnected, recreatedMemory, "", options);
                var restored = await new EpisodeMetadataProvider(recreated, NullLogger<EpisodeMetadataProvider>.Instance, disconnected)
                    .GetMetadata(episodeInfo, CancellationToken.None);
                Assert.True(restored.HasMetadata);
                Assert.Equal("Пилот / Продолжение", restored.Item.Name);
                Assert.Equal("Pilot / Next", restored.Item.OriginalTitle);
                Assert.Contains("Описание второй серии", restored.Item.Overview);
                Assert.Equal(2, restored.Item.IndexNumberEnd);
                Assert.Equal(new DateTime(2019, 5, 2), restored.Item.PremiereDate?.Date);
                Assert.Empty(disconnected.Requests);
            }
            finally
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, recursive: true);
            }
        }

        [Theory]
        [InlineData(true)]
        [InlineData(false)]
        public async Task ShouldResolveTrailerThroughRealClientsAndReuseMemoryCache(bool validManifest)
        {
            using var recorded = new FixtureHttpFactory();
            recorded.Add(ApiRoot + "/api/v2.2/films/5273/videos", VideosJson);
            recorded.Add(WidgetUrl, "{\"src\":\"" + ManifestUrl + "\"}", "text/html");
            recorded.Add(ManifestUrl, validManifest ? "#EXTM3U\n#EXT-X-VERSION:3\n" : "<html>Unavailable</html>", "text/plain");
            using var memory = new MemoryCache(new MemoryCacheOptions());
            var client = CreateClient(recorded, memory, "offline-fixture-token", new KinopoiskCacheOptions());
            var resolver = new KinopoiskTrailerStreamResolver(recorded, memory, NullLogger<KinopoiskTrailerStreamResolver>.Instance);
            var service = new KinopoiskTrailerPlaybackService(client, resolver, NullLogger<KinopoiskTrailerPlaybackService>.Instance);

            var first = await service.Get(5273, CancellationToken.None);
            var second = await service.Get(5273, CancellationToken.None);

            Assert.NotNull(first);
            Assert.Equal(validManifest ? "hls" : "youtube", first.Selected.PlaybackKind);
            Assert.Equal(first.Selected.Url, second.Selected.Url);
            if (validManifest)
            {
                Assert.Equal(ManifestUrl, first.Selected.Url);
                Assert.Equal(WidgetUrl, first.Selected.RequestHeaders["Referer"]);
                Assert.Equal("youtube", Assert.Single(first.Fallbacks).Provider);
            }
            else
            {
                Assert.Equal("YOUTUBE001", first.Selected.VideoId);
            }

            Assert.Equal(3, recorded.Requests.Count);
            Assert.True(recorded.Requests[0].HasApiKey);
            Assert.All(recorded.Requests.Skip(1), request => Assert.False(request.HasApiKey));
            Assert.Equal(WidgetUrl, recorded.Requests[2].Referer);
            Assert.Equal("https://widgets.kinopoisk.ru", recorded.Requests[2].Origin);
        }

        private static FixtureHttpFactory CreateMovieFixture()
        {
            var fixture = new FixtureHttpFactory();
            fixture.Add(ApiRoot + "/api/v2.2/films/5273", FilmJson);
            fixture.Add(ApiRoot + "/api/v2.2/films/5273/distributions", DistributionsJson);
            fixture.Add(ApiRoot + "/api/v1/staff?filmId=5273", "[]");
            fixture.Add(ApiRoot + "/api/v2.2/films/5273/videos", "{\"total\":0,\"items\":[]}");
            return fixture;
        }

        private static CachedKinopoiskApiClient CreateClient(FixtureHttpFactory factory, IMemoryCache memory,
            string token, KinopoiskCacheOptions options, KinopoiskDiagnostics diagnostics = null)
            => new(new KinopoiskApiClient(token, NullLogger<KinopoiskApiClient>.Instance, factory), memory,
                NullLogger<CachedKinopoiskApiClient>.Instance, options, diagnostics ?? new KinopoiskDiagnostics());

        private static MovieMetadataProvider CreateMovieProvider(IKinopoiskApiClient client, IHttpClientFactory factory)
            => new(client, new CommonResolver<MovieInfo>(NullLogger<CommonResolver<MovieInfo>>.Instance),
                NullLogger<MovieMetadataProvider>.Instance, factory);

        private static MovieInfo CreateMovieInfo()
        {
            var info = new MovieInfo { Name = "Шрэк 2", Year = 2004 };
            info.SetProviderId(Constants.ProviderId, "5273");
            info.SetProviderId("Imdb", "tt0298148");
            info.SetProviderId("Tmdb", "809");
            return info;
        }

        private static void AssertMovie(MetadataResult<MediaBrowser.Controller.Entities.Movies.Movie> result)
        {
            Assert.True(result.HasMetadata);
            Assert.Equal("Шрэк 2", result.Item.Name);
            Assert.Equal("Shrek 2", result.Item.OriginalTitle);
            Assert.Equal("5273", result.Item.GetProviderId(Constants.ProviderId));
            Assert.Equal("tt0298148", result.Item.GetProviderId("Imdb"));
            Assert.Equal("809", result.Item.GetProviderId("Tmdb"));
            Assert.Equal("Продолжение приключений Шрэка.", result.Item.Overview);
            Assert.Contains("мультфильм", result.Item.Genres);
            Assert.Equal(8.1f, result.Item.CommunityRating);
            Assert.Equal(new DateTime(2004, 5, 19), result.Item.PremiereDate?.Date);
        }

        private sealed record RecordedRequest(bool HasApiKey, string Referer, string Origin);

        private sealed class FixtureHttpFactory : HttpMessageHandler, IHttpClientFactory
        {
            private readonly Dictionary<string, (string Body, string ContentType)> _responses = new();
            private readonly List<HttpClient> _clients = new();
            public List<RecordedRequest> Requests { get; } = new();

            public void Add(string url, string body, string contentType = "application/json")
                => _responses.Add(url, (body, contentType));

            public HttpClient CreateClient(string name)
            {
                var client = new HttpClient(this, disposeHandler: false);
                _clients.Add(client);
                return client;
            }

            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Requests.Add(new RecordedRequest(request.Headers.Contains("X-API-KEY"),
                    request.Headers.Referrer?.AbsoluteUri,
                    request.Headers.TryGetValues("Origin", out var origins) ? origins.Single() : null));
                if (!_responses.TryGetValue(request.RequestUri.AbsoluteUri, out var response))
                    throw new InvalidOperationException("Для запроса отсутствует автономный ответ.");
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(response.Body, Encoding.UTF8, response.ContentType)
                });
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing)
                    foreach (var client in _clients)
                        client.Dispose();
                base.Dispose(disposing);
            }
        }
    }
}
