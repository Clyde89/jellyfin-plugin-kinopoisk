using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    [CollectionDefinition("Native trailer library", DisableParallelization = true)]
    public class NativeTrailerLibraryCollection
    {
    }

    [Collection("Native trailer library")]
    public class NativeTrailerPersistenceTests : IDisposable
    {
        private const int KinopoiskId = 301;
        private readonly ILibraryManager _previousLibraryManager = BaseItem.LibraryManager;
        private readonly Dictionary<Guid, BaseItem> _items = new();
        private readonly Mock<ILibraryManager> _library = new();
        private readonly Mock<IItemPersistenceService> _persistence = new(MockBehavior.Strict);
        private readonly Mock<IKinopoiskNativeTrailerCache> _cache = new();
        private readonly Mock<IKinopoiskTrailerPlaybackService> _playback = new(MockBehavior.Strict);
        private readonly Movie _movie = CreateMovie(Guid.NewGuid());
        private readonly KinopoiskNativeTrailerBridge _bridge;

        public NativeTrailerPersistenceTests()
        {
            _items.Add(_movie.Id, _movie);
            _library.Setup(service => service.GetItemById(It.IsAny<Guid>()))
                .Returns((Guid id) => _items.GetValueOrDefault(id));
            _library.Setup(service => service.GetItemList(It.IsAny<InternalItemsQuery>()))
                .Returns((InternalItemsQuery query) => _items.Values.Where(item =>
                    (query.OwnerIds.Length == 0 || query.OwnerIds.Contains(item.OwnerId))
                    && (query.ExtraTypes.Length == 0 || query.ExtraTypes.Contains(item.ExtraType.GetValueOrDefault()))
                    && (query.IncludeItemTypes.Length == 0 || query.IncludeItemTypes.Contains(item.GetBaseItemKind())))
                    .ToArray());
            _library.Setup(service => service.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
                .Callback((BaseItem item, BaseItem parent) => _items[item.Id] = item);
            _library.Setup(service => service.DeleteItem(
                    It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()))
                .Callback((BaseItem item, DeleteOptions options, BaseItem parent, bool notify) => _items.Remove(item.Id));
            _cache.Setup(service => service.GetSnapshot()).Returns(new KinopoiskNativeTrailerCacheSnapshot());
            BaseItem.LibraryManager = _library.Object;
            _bridge = new KinopoiskNativeTrailerBridge(
                _library.Object,
                _persistence.Object,
                _playback.Object,
                _cache.Object,
                NullLogger<KinopoiskNativeTrailerBridge>.Instance);
        }

        [Fact]
        public async Task ShouldRestoreCachedTrailerWhenWidgetMetadataIsMissing()
        {
            _movie.RemoteTrailers = Array.Empty<MediaUrl>();
            _cache.Setup(service => service.TryGet(KinopoiskId, false))
                .Returns(new KinopoiskNativeTrailerCacheEntry { Path = "cached-trailer.mp4", RunTimeTicks = 123 });

            Assert.Equal(KinopoiskId, await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));
            Assert.Equal(KinopoiskId, await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));

            var trailer = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(KinopoiskNativeTrailerBridge.CreateAndroidTvTrailerId(_movie.Id, KinopoiskId), trailer.Id);
            Assert.Equal(_movie.Id, trailer.OwnerId);
            Assert.Equal("cached-trailer.mp4", trailer.Path);
            Assert.Equal(123, trailer.RunTimeTicks);
            _library.Verify(service => service.CreateItem(trailer, _movie), Times.Once);
            _persistence.VerifyNoOtherCalls();
            _playback.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldNotCreateTrailerWithoutWidgetOrReadyCache()
        {
            _movie.RemoteTrailers = Array.Empty<MediaUrl>();

            Assert.Null(await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));

            Assert.Empty(_items.Values.OfType<Trailer>());
            _library.Verify(service => service.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
            _persistence.VerifyNoOtherCalls();
            _playback.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldRecreateCachedTrailerWithStableIdAfterWidgetAndExtraAreLost()
        {
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var previous = Assert.Single(_items.Values.OfType<Trailer>());
            _items.Remove(previous.Id);
            _movie.RemoteTrailers = Array.Empty<MediaUrl>();
            _cache.Setup(service => service.TryGet(KinopoiskId, false))
                .Returns(new KinopoiskNativeTrailerCacheEntry { Path = "cached-trailer.mp4" });

            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            var recreated = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(previous.Id, recreated.Id);
            Assert.Equal("cached-trailer.mp4", recreated.Path);
            Assert.Same(_movie, _items[_movie.Id]);
            _persistence.VerifyNoOtherCalls();
            _playback.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldRecreateTrailerWithStableIdAfterLibraryDropsExtra()
        {
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var previous = Assert.Single(_items.Values.OfType<Trailer>());
            _items.Remove(previous.Id);

            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            var recreated = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(previous.Id, recreated.Id);
            Assert.Equal(_movie.Id, recreated.OwnerId);
            Assert.Same(_movie, _items[_movie.Id]);
            _library.Verify(service => service.CreateItem(It.IsAny<BaseItem>(), _movie), Times.Exactly(2));
            _persistence.VerifyNoOtherCalls();
            _playback.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldReuseRegisteredCachedTrailerAfterBridgeRecreationWithoutApi()
        {
            _cache.Setup(service => service.TryGet(KinopoiskId, false))
                .Returns(new KinopoiskNativeTrailerCacheEntry { Path = "cached-trailer.mp4", RunTimeTicks = 123 });
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var previous = Assert.Single(_items.Values.OfType<Trailer>());
            var recreatedBridge = new KinopoiskNativeTrailerBridge(
                _library.Object, _persistence.Object, _playback.Object, _cache.Object,
                NullLogger<KinopoiskNativeTrailerBridge>.Instance);

            await recreatedBridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            var reused = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Same(previous, reused);
            Assert.Equal("cached-trailer.mp4", reused.Path);
            Assert.Equal(123, reused.RunTimeTicks);
            _library.Verify(service => service.CreateItem(It.IsAny<BaseItem>(), _movie), Times.Once);
            _persistence.VerifyNoOtherCalls();
            _playback.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldRegisterOwnedTrailerOnceWithoutSavingMovie()
        {
            Assert.Equal(KinopoiskId, await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));
            Assert.Equal(KinopoiskId, await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));

            var trailer = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(_movie.Id, trailer.OwnerId);
            Assert.Equal(ExtraType.Trailer, trailer.ExtraType);
            Assert.Equal(KinopoiskNativeTrailerBridge.CreateAndroidTvTrailerId(_movie.Id, KinopoiskId), trailer.Id);
            Assert.True(KinopoiskNativeTrailerBridge.IsAndroidTvPlayable(trailer));
            _library.Verify(service => service.CreateItem(trailer, _movie), Times.Once);
            _persistence.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldFindMarkedTrailerThroughOwnerQuery()
        {
            var trailer = KinopoiskNativeTrailerBridge.CreateTrailer(_movie, KinopoiskId, "Трейлер");
            trailer.Id = Guid.NewGuid();
            _items.Add(trailer.Id, trailer);

            var status = await _bridge.GetStatus(KinopoiskId, _movie.Id, CancellationToken.None);

            Assert.True(status.Registered);
            Assert.Equal(trailer.Id, status.TrailerId);
            Assert.Equal(1, status.LocalTrailerCount);
            _library.Verify(service => service.GetItemList(It.Is<InternalItemsQuery>(query =>
                query.OwnerIds.SequenceEqual(new[] { _movie.Id })
                && query.ExtraTypes.SequenceEqual(new[] { ExtraType.Trailer }))), Times.AtLeastOnce);
        }

        [Fact]
        public async Task ShouldPreserveExistingMovieAndTrailerIdsForLegacyReplacement()
        {
            var legacy = AddLegacyTrailer(KinopoiskNativeTrailerBridge.CreateTrailerId(_movie.Id, KinopoiskId));

            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            var replacement = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(KinopoiskNativeTrailerBridge.CreateAndroidTvTrailerId(_movie.Id, KinopoiskId), replacement.Id);
            Assert.Equal(_movie.Id, replacement.OwnerId);
            Assert.Same(_movie, _items[_movie.Id]);
            Assert.False(_items.ContainsKey(legacy.Id));
            VerifySafeDeletion(legacy);
        }

        [Fact]
        public async Task ShouldKeepReplacementWhenLegacyTypeHasCurrentId()
        {
            var legacy = AddLegacyTrailer(KinopoiskNativeTrailerBridge.CreateAndroidTvTrailerId(_movie.Id, KinopoiskId));

            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            var replacement = Assert.Single(_items.Values.OfType<Trailer>());
            Assert.Equal(legacy.Id, replacement.Id);
            Assert.True(KinopoiskNativeTrailerBridge.IsAndroidTvPlayable(replacement));
            _library.Verify(service => service.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task ShouldKeepLegacyTrailerWhenReplacementCreationFails()
        {
            var legacy = AddLegacyTrailer(KinopoiskNativeTrailerBridge.CreateTrailerId(_movie.Id, KinopoiskId));
            _library.Setup(service => service.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()))
                .Throws(new InvalidOperationException("Сохранение не выполнено."));

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));

            Assert.Same(legacy, Assert.Single(_items.Values.OfType<Trailer>()));
            _library.Verify(service => service.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task ShouldPersistOnlyChangedCachedTrailerThroughNewService()
        {
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var trailer = Assert.Single(_items.Values.OfType<Trailer>());
            _cache.Setup(service => service.TryGet(KinopoiskId, false))
                .Returns(new KinopoiskNativeTrailerCacheEntry { Path = "cached-trailer.mp4", RunTimeTicks = 123 });
            _persistence.Setup(service => service.SaveItems(
                It.IsAny<IReadOnlyList<BaseItem>>(), It.IsAny<CancellationToken>()));

            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);

            Assert.Equal("cached-trailer.mp4", trailer.Path);
            Assert.Equal(123, trailer.RunTimeTicks);
            _persistence.Verify(service => service.SaveItems(
                It.Is<IReadOnlyList<BaseItem>>(items => items.Count == 1 && ReferenceEquals(items[0], trailer)),
                CancellationToken.None), Times.Once);
            _persistence.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldRemoveOnlyOwnedMarkedTrailerWithoutDeletingFiles()
        {
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var trailer = Assert.Single(_items.Values.OfType<Trailer>());
            var regular = new Trailer { Id = Guid.NewGuid(), OwnerId = _movie.Id, ExtraType = ExtraType.Trailer };
            _items.Add(regular.Id, regular);
            var otherMovie = CreateMovie(Guid.NewGuid());
            _items.Add(otherMovie.Id, otherMovie);
            var otherTrailer = KinopoiskNativeTrailerBridge.CreateTrailer(otherMovie, KinopoiskId, "Трейлер");
            _items.Add(otherTrailer.Id, otherTrailer);

            var result = await _bridge.Remove(KinopoiskId, _movie.Id, CancellationToken.None);

            Assert.Equal("removed", result.State);
            Assert.False(_items.ContainsKey(trailer.Id));
            Assert.Same(regular, _items[regular.Id]);
            Assert.Same(otherTrailer, _items[otherTrailer.Id]);
            Assert.Same(_movie, _items[_movie.Id]);
            VerifySafeDeletion(trailer);
            _persistence.VerifyNoOtherCalls();
        }

        [Fact]
        public async Task ShouldNotDeleteLegacyMarkerOwnedByAnotherMovie()
        {
            await _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None);
            var foreign = AddLegacyTrailer(KinopoiskNativeTrailerBridge.CreateTrailerId(_movie.Id, KinopoiskId));
            foreign.OwnerId = Guid.NewGuid();

            await _bridge.Remove(KinopoiskId, _movie.Id, CancellationToken.None);

            Assert.Same(foreign, _items[foreign.Id]);
        }

        [Fact]
        public async Task ShouldRejectIdCollisionWithoutOverwritingForeignItem()
        {
            var foreign = new Movie { Id = KinopoiskNativeTrailerBridge.CreateAndroidTvTrailerId(_movie.Id, KinopoiskId) };
            _items.Add(foreign.Id, foreign);

            await Assert.ThrowsAsync<InvalidOperationException>(() =>
                _bridge.EnsureLazyRegistered(_movie.Id, CancellationToken.None));

            Assert.Same(foreign, _items[foreign.Id]);
            _library.Verify(service => service.CreateItem(It.IsAny<BaseItem>(), It.IsAny<BaseItem>()), Times.Never);
        }

        [Fact]
        public async Task ShouldRefuseAmbiguousMovieSelection()
        {
            var duplicate = CreateMovie(Guid.NewGuid());
            _items.Add(duplicate.Id, duplicate);

            var result = await _bridge.Remove(KinopoiskId, null, CancellationToken.None);

            Assert.Equal("ambiguous-movie", result.State);
            _library.Verify(service => service.DeleteItem(
                It.IsAny<BaseItem>(), It.IsAny<DeleteOptions>(), It.IsAny<BaseItem>(), It.IsAny<bool>()), Times.Never);
        }

        [Fact]
        public async Task ShouldNotCreateTrailerAfterCancellation()
        {
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                _bridge.EnsureLazyRegistered(_movie.Id, cancellation.Token));

            Assert.Empty(_items.Values.OfType<Trailer>());
        }

        public void Dispose() => BaseItem.LibraryManager = _previousLibraryManager;

        private static Movie CreateMovie(Guid id)
        {
            var movie = new Movie { Id = id };
            movie.ProviderIds[Constants.ProviderId] = KinopoiskId.ToString();
            movie.RemoteTrailers = new[] { new MediaUrl { Url = "https://widgets.kinopoisk.ru/discovery/film/301/trailer/1", Name = "Трейлер" } };
            return movie;
        }

        private Trailer AddLegacyTrailer(Guid id)
        {
            var trailer = new Trailer { Id = id, OwnerId = _movie.Id, ExtraType = ExtraType.Trailer, IsVirtualItem = true };
            trailer.ProviderIds[KinopoiskNativeTrailerBridge.BridgeProviderId] = KinopoiskId.ToString();
            _items.Add(id, trailer);
            return trailer;
        }

        private void VerifySafeDeletion(Trailer trailer)
            => _library.Verify(service => service.DeleteItem(
                trailer,
                It.Is<DeleteOptions>(options => !options.DeleteFileLocation && !options.DeleteFromExternalProvider),
                _movie,
                It.IsAny<bool>()), Times.Once);
    }
}
