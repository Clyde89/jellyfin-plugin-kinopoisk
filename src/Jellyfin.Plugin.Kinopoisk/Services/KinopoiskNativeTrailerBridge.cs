#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kinopoisk.Playback;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Регистрирует динамический LocalTrailer, видимый штатными клиентами Jellyfin.
    /// </summary>
    public sealed class KinopoiskNativeTrailerBridge
    {
        public const string BridgeProviderId = "kinopoisk-native-trailer";

        private const string ExternalIdPrefix = "kinopoisk-native-trailer:";
        private const string AndroidTvExternalIdPrefix = "kinopoisk-native-trailer:androidtv-v2:";

        private readonly ILibraryManager _libraryManager;
        private readonly IItemRepository _itemRepository;
        private readonly IKinopoiskTrailerPlaybackService _playbackService;
        private readonly IKinopoiskNativeTrailerCache _cache;
        private readonly ILogger<KinopoiskNativeTrailerBridge> _logger;
        private readonly SemaphoreSlim _gate = new(1, 1);

        public KinopoiskNativeTrailerBridge(
            ILibraryManager libraryManager,
            IItemRepository itemRepository,
            IKinopoiskTrailerPlaybackService playbackService,
            IKinopoiskNativeTrailerCache cache,
            ILogger<KinopoiskNativeTrailerBridge> logger)
        {
            _libraryManager = libraryManager
                ?? throw new ArgumentNullException(nameof(libraryManager));
            _itemRepository = itemRepository
                ?? throw new ArgumentNullException(nameof(itemRepository));
            _playbackService = playbackService
                ?? throw new ArgumentNullException(nameof(playbackService));
            _cache = cache ?? throw new ArgumentNullException(nameof(cache));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public static bool IsAllowed(int kinopoiskId)
            => kinopoiskId > 0;

        public static bool TryGetKinopoiskId(BaseItem item, out int kinopoiskId)
        {
            kinopoiskId = 0;
            return item is Trailer
                && item.ExtraType == ExtraType.Trailer
                && item.ProviderIds.TryGetValue(BridgeProviderId, out var value)
                && int.TryParse(value, out kinopoiskId)
                && IsAllowed(kinopoiskId);
        }

        internal static bool TrySelectKinopoiskWidget(
            IReadOnlyList<MediaUrl>? trailers,
            out MediaUrl? selected)
        {
            selected = trailers?.FirstOrDefault(trailer =>
                Uri.TryCreate(trailer?.Url, UriKind.Absolute, out var uri)
                && uri.Scheme == Uri.UriSchemeHttps
                && string.Equals(
                    uri.IdnHost.TrimEnd('.'),
                    "widgets.kinopoisk.ru",
                    StringComparison.OrdinalIgnoreCase));
            return selected is not null;
        }

        public Task<KinopoiskNativeTrailerBridgeResult> GetStatus(
            int kinopoiskId,
            Guid? movieId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var movies = FindMovies(kinopoiskId, movieId);
            if (movies.Count == 0)
                return Task.FromResult(CreateResult(kinopoiskId, "movie-not-found"));
            if (movies.Count > 1)
                return Task.FromResult(CreateResult(kinopoiskId, "ambiguous-movie"));

            var movie = movies[0];
            var trailer = FindBridgeTrailer(movie, kinopoiskId);
            return Task.FromResult(CreateResult(
                kinopoiskId,
                trailer is null
                    ? "not-registered"
                    : IsAndroidTvPlayable(trailer)
                        ? _cache.TryGet(kinopoiskId, false) is not null
                            ? "ready-local-cache"
                            : "ready-fallback-cache-missing"
                        : "androidtv-location-blocked",
                movie,
                trailer));
        }

        public async Task<KinopoiskNativeTrailerBridgeResult> Prepare(
            int kinopoiskId,
            Guid? movieId,
            CancellationToken cancellationToken)
        {
            if (!IsAllowed(kinopoiskId))
                return CreateResult(kinopoiskId, "not-allowed");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var movies = FindMovies(kinopoiskId, movieId);
                if (movies.Count == 0)
                    return CreateResult(kinopoiskId, "movie-not-found");
                if (movies.Count > 1)
                    return CreateResult(kinopoiskId, "ambiguous-movie");

                var movie = movies[0];
                var existing = FindBridgeTrailer(movie, kinopoiskId);
                var playback = await _playbackService
                    .Get(kinopoiskId, cancellationToken)
                    .ConfigureAwait(false);
                if (!IsNativeHls(playback))
                    return CreateResult(
                        kinopoiskId,
                        "kinopoisk-hls-unavailable",
                        movie,
                        existing);

                var cached = await _cache
                    .GetOrCreate(
                        kinopoiskId,
                        playback!.Selected,
                        cancellationToken)
                    .ConfigureAwait(false);
                var trailer = EnsureRegistered(
                    movie,
                    kinopoiskId,
                    playback.Selected.Name,
                    cached,
                    cancellationToken);
                return CreateResult(
                    kinopoiskId,
                    cached is null ? "ready-fallback-cache-disabled" : "ready-local-cache",
                    movie,
                    trailer);
            }
            finally
            {
                _gate.Release();
            }
        }

        /// <summary>
        /// Быстро регистрирует LocalTrailer без сетевого ожидания. Сам MP4 может
        /// подготавливаться фоновым сервисом уже после возврата карточки клиенту.
        /// </summary>
        public async Task<int?> EnsureLazyRegistered(
            Guid movieId,
            CancellationToken cancellationToken)
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_libraryManager.GetItemById(movieId) is not Movie movie
                    || !TryGetMovieKinopoiskId(movie, out var kinopoiskId))
                {
                    return null;
                }

                var existing = FindBridgeTrailer(movie, kinopoiskId);
                if (!TrySelectKinopoiskWidget(movie.RemoteTrailers, out var source))
                    return existing is null ? null : kinopoiskId;

                EnsureRegistered(
                    movie,
                    kinopoiskId,
                    source!.Name,
                    _cache.TryGet(kinopoiskId, false),
                    cancellationToken);
                return kinopoiskId;
            }
            finally
            {
                _gate.Release();
            }
        }

        public async Task<KinopoiskNativeTrailerBridgeResult> Remove(
            int kinopoiskId,
            Guid? movieId,
            CancellationToken cancellationToken)
        {
            if (!IsAllowed(kinopoiskId))
                return CreateResult(kinopoiskId, "not-allowed");

            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var movies = FindMovies(kinopoiskId, movieId);
                if (movies.Count == 0)
                    return CreateResult(kinopoiskId, "movie-not-found");
                if (movies.Count > 1)
                    return CreateResult(kinopoiskId, "ambiguous-movie");

                var movie = movies[0];
                var trailer = FindBridgeTrailer(movie, kinopoiskId);
                if (trailer is null)
                    return CreateResult(kinopoiskId, "not-registered", movie);

                var previousExtraIds = movie.ExtraIds;
                try
                {
                    movie.ExtraIds = previousExtraIds
                        .Where(id => id != trailer.Id)
                        .ToArray();
                    SaveMovie(movie, cancellationToken);
                }
                catch
                {
                    movie.ExtraIds = previousExtraIds;
                    throw;
                }
                _libraryManager.DeleteItem(
                    trailer,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    },
                    movie,
                    true);
                DeleteLegacyTrailerIfPresent(movie, kinopoiskId);

                _logger.LogInformation(
                    "NativeTrailerBridge удалил локальный трейлер {TrailerId} для фильма {MovieId} и Kinopoisk ID {KinopoiskId}",
                    trailer.Id,
                    movie.Id,
                    kinopoiskId);
                return CreateResult(kinopoiskId, "removed", movie);
            }
            finally
            {
                _gate.Release();
            }
        }

        internal static Guid CreateTrailerId(Guid movieId, int kinopoiskId)
        {
            var input = Encoding.UTF8.GetBytes(
                $"{ExternalIdPrefix}{kinopoiskId}:{movieId:N}");
            var digest = SHA256.HashData(input);
            return new Guid(digest.AsSpan(0, 16));
        }

        internal static Guid CreateAndroidTvTrailerId(Guid movieId, int kinopoiskId)
        {
            var input = Encoding.UTF8.GetBytes(
                $"{AndroidTvExternalIdPrefix}{kinopoiskId}:{movieId:N}");
            var digest = SHA256.HashData(input);
            return new Guid(digest.AsSpan(0, 16));
        }

        internal static bool IsAndroidTvPlayable(Trailer trailer)
            => trailer is KinopoiskNativeTrailerItem
                && trailer.LocationType != LocationType.Virtual;

        private static bool IsNativeHls(KinopoiskTrailerPlaybackResponse? playback)
            => playback is not null
                && string.Equals(
                    playback.Selected.Provider,
                    "kinopoisk",
                    StringComparison.OrdinalIgnoreCase)
                && string.Equals(
                    playback.Selected.PlaybackKind,
                    "hls",
                    StringComparison.OrdinalIgnoreCase)
                && Uri.TryCreate(
                    playback.Selected.Url,
                    UriKind.Absolute,
                    out var mediaUri)
                && mediaUri.Scheme == Uri.UriSchemeHttps;

        internal static bool MatchesSelection(
            Movie movie,
            int kinopoiskId,
            Guid? movieId)
            => HasKinopoiskId(movie, kinopoiskId)
                && (!movieId.HasValue || movie.Id == movieId.Value);

        internal static bool TryGetMovieKinopoiskId(Movie movie, out int kinopoiskId)
        {
            kinopoiskId = 0;
            return movie.ProviderIds.TryGetValue(Constants.ProviderId, out var value)
                && int.TryParse(value, out kinopoiskId)
                && IsAllowed(kinopoiskId);
        }

        private IReadOnlyList<Movie> FindMovies(int kinopoiskId, Guid? movieId)
        {
            if (movieId.HasValue)
            {
                var selectedMovie = _libraryManager.GetItemById(movieId.Value) as Movie;
                return selectedMovie is not null
                    && MatchesSelection(selectedMovie, kinopoiskId, movieId)
                        ? new[] { selectedMovie }
                        : Array.Empty<Movie>();
            }

            var indexedMatches = _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    Limit = 3,
                    EnableTotalRecordCount = false,
                    HasAnyProviderId = new Dictionary<string, string>(
                        StringComparer.OrdinalIgnoreCase)
                    {
                        [Constants.ProviderId] = kinopoiskId.ToString()
                    }
                })
                .OfType<Movie>()
                .Where(movie => MatchesSelection(movie, kinopoiskId, movieId))
                .Take(3)
                .ToArray();
            if (indexedMatches.Length > 0)
                return indexedMatches;

            _logger.LogDebug(
                "Индексированный поиск не нашёл фильм с Kinopoisk ID {KinopoiskId}; выполняется точная резервная проверка фильмов",
                kinopoiskId);
            return _libraryManager
                .GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { BaseItemKind.Movie },
                    Recursive = true,
                    EnableTotalRecordCount = false
                })
                .OfType<Movie>()
                .Where(movie => MatchesSelection(movie, kinopoiskId, movieId))
                .Take(3)
                .ToArray();
        }

        private static bool HasKinopoiskId(Movie movie, int kinopoiskId)
            => movie.ProviderIds.TryGetValue(
                Constants.ProviderId,
                out var value)
                && string.Equals(
                    value,
                    kinopoiskId.ToString(),
                    StringComparison.Ordinal);

        private Trailer? FindBridgeTrailer(Movie movie, int kinopoiskId)
        {
            var androidTvItem = _libraryManager.GetItemById(
                CreateAndroidTvTrailerId(movie.Id, kinopoiskId));
            if (androidTvItem is Trailer androidTvTrailer
                && androidTvTrailer.OwnerId == movie.Id
                && TryGetKinopoiskId(androidTvTrailer, out var androidTvId)
                && androidTvId == kinopoiskId)
            {
                return androidTvTrailer;
            }

            var deterministicItem = _libraryManager.GetItemById(
                CreateTrailerId(movie.Id, kinopoiskId));
            if (deterministicItem is Trailer deterministicTrailer
                && deterministicTrailer.OwnerId == movie.Id
                && TryGetKinopoiskId(deterministicTrailer, out var deterministicId)
                && deterministicId == kinopoiskId)
            {
                return deterministicTrailer;
            }

            return movie.ExtraIds
                .Select(_libraryManager.GetItemById)
                .OfType<Trailer>()
                .FirstOrDefault(item => TryGetKinopoiskId(item, out var value)
                    && value == kinopoiskId);
        }

        internal static KinopoiskNativeTrailerItem CreateTrailer(
            Movie movie,
            int kinopoiskId,
            string sourceName)
        {
            var now = DateTime.UtcNow;
            var trailer = new KinopoiskNativeTrailerItem
            {
                Id = CreateAndroidTvTrailerId(movie.Id, kinopoiskId),
                OwnerId = movie.Id,
                ParentId = Guid.Empty,
                Name = string.IsNullOrWhiteSpace(sourceName)
                    ? "Трейлер КиноПоиска"
                    : sourceName,
                ExternalId = $"{ExternalIdPrefix}{kinopoiskId}",
                ExtraType = ExtraType.Trailer,
                IsVirtualItem = false,
                VideoType = VideoType.VideoFile,
                DateCreated = now,
                DateModified = now,
                DateLastRefreshed = now
            };
            trailer.ProviderIds[BridgeProviderId] = kinopoiskId.ToString();
            return trailer;
        }

        private Trailer EnsureRegistered(
            Movie movie,
            int kinopoiskId,
            string sourceName,
            KinopoiskNativeTrailerCacheEntry? cached,
            CancellationToken cancellationToken)
        {
            var existing = FindBridgeTrailer(movie, kinopoiskId);
            if (existing is not null)
            {
                if (!IsAndroidTvPlayable(existing))
                {
                    return ReplaceBlockedTrailer(
                        movie,
                        existing,
                        kinopoiskId,
                        sourceName,
                        cached,
                        cancellationToken);
                }

                if (ApplyCachedPath(existing, cached))
                    SaveTrailer(existing, cancellationToken);
                AttachTrailer(movie, existing, cancellationToken);
                return existing;
            }

            var trailer = CreateTrailer(movie, kinopoiskId, sourceName);
            ApplyCachedPath(trailer, cached);
            var created = false;
            var previousExtraIds = movie.ExtraIds;
            try
            {
                _libraryManager.CreateItem(trailer, movie);
                created = true;
                movie.ExtraIds = previousExtraIds
                    .Append(trailer.Id)
                    .Distinct()
                    .ToArray();
                SaveMovie(movie, cancellationToken);
            }
            catch
            {
                movie.ExtraIds = previousExtraIds;
                if (created)
                {
                    _libraryManager.DeleteItem(
                        trailer,
                        new DeleteOptions
                        {
                            DeleteFileLocation = false,
                            DeleteFromExternalProvider = false
                        },
                        movie,
                        false);
                }

                throw;
            }

            _logger.LogInformation(
                "NativeTrailerBridge зарегистрировал LocalTrailer {TrailerId} для фильма {MovieId} и Kinopoisk ID {KinopoiskId}",
                trailer.Id,
                movie.Id,
                kinopoiskId);
            return trailer;
        }

        private void AttachTrailer(
            Movie movie,
            Trailer trailer,
            CancellationToken cancellationToken)
        {
            if (movie.ExtraIds.Contains(trailer.Id))
                return;

            var originalExtraIds = movie.ExtraIds;
            try
            {
                movie.ExtraIds = originalExtraIds
                    .Append(trailer.Id)
                    .Distinct()
                    .ToArray();
                SaveMovie(movie, cancellationToken);
            }
            catch
            {
                movie.ExtraIds = originalExtraIds;
                throw;
            }
        }

        private Trailer ReplaceBlockedTrailer(
            Movie movie,
            Trailer blockedTrailer,
            int kinopoiskId,
            string sourceName,
            KinopoiskNativeTrailerCacheEntry? cached,
            CancellationToken cancellationToken)
        {
            var replacement = CreateTrailer(
                movie,
                kinopoiskId,
                sourceName);
            ApplyCachedPath(replacement, cached);
            var previousExtraIds = movie.ExtraIds;
            var created = false;
            try
            {
                _libraryManager.CreateItem(replacement, movie);
                created = true;
                movie.ExtraIds = previousExtraIds
                    .Where(id => id != blockedTrailer.Id)
                    .Append(replacement.Id)
                    .Distinct()
                    .ToArray();
                SaveMovie(movie, cancellationToken);
            }
            catch
            {
                movie.ExtraIds = previousExtraIds;
                if (created)
                {
                    _libraryManager.DeleteItem(
                        replacement,
                        new DeleteOptions
                        {
                            DeleteFileLocation = false,
                            DeleteFromExternalProvider = false
                        },
                        movie,
                        false);
                }

                throw;
            }

            try
            {
                _libraryManager.DeleteItem(
                    blockedTrailer,
                    new DeleteOptions
                    {
                        DeleteFileLocation = false,
                        DeleteFromExternalProvider = false
                    },
                    movie,
                    false);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "NativeTrailerBridge заменил заблокированный Android TV трейлер {OldTrailerId}, но не смог удалить его отвязанный элемент",
                    blockedTrailer.Id);
            }

            _logger.LogInformation(
                "NativeTrailerBridge заменил виртуальный трейлер {OldTrailerId} на Android TV-совместимый {TrailerId} для фильма {MovieId}",
                blockedTrailer.Id,
                replacement.Id,
                movie.Id);
            return replacement;
        }

        private void DeleteLegacyTrailerIfPresent(Movie movie, int kinopoiskId)
        {
            var legacy = _libraryManager.GetItemById(
                CreateTrailerId(movie.Id, kinopoiskId)) as Trailer;
            if (legacy is null
                || !TryGetKinopoiskId(legacy, out var legacyKinopoiskId)
                || legacyKinopoiskId != kinopoiskId)
            {
                return;
            }

            _libraryManager.DeleteItem(
                legacy,
                new DeleteOptions
                {
                    DeleteFileLocation = false,
                    DeleteFromExternalProvider = false
                },
                movie,
                false);
        }

        private void SaveMovie(Movie movie, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            movie.DateLastSaved = DateTime.UtcNow;
            _itemRepository.SaveItems(new[] { movie }, cancellationToken);
        }

        private void SaveTrailer(Trailer trailer, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            trailer.DateModified = DateTime.UtcNow;
            trailer.DateLastSaved = DateTime.UtcNow;
            _itemRepository.SaveItems(new BaseItem[] { trailer }, cancellationToken);
        }

        private static bool ApplyCachedPath(
            Trailer trailer,
            KinopoiskNativeTrailerCacheEntry? cached)
        {
            if (cached is null)
                return false;

            var changed = !string.Equals(
                trailer.Path,
                cached.Path,
                StringComparison.Ordinal);
            trailer.Path = cached.Path;
            if (cached.RunTimeTicks.HasValue
                && trailer.RunTimeTicks != cached.RunTimeTicks)
            {
                trailer.RunTimeTicks = cached.RunTimeTicks;
                changed = true;
            }

            return changed;
        }

        private KinopoiskNativeTrailerBridgeResult CreateResult(
            int kinopoiskId,
            string state,
            Movie? movie = null,
            Trailer? trailer = null)
        {
            var cached = _cache.TryGet(kinopoiskId, false);
            var snapshot = _cache.GetSnapshot();
            return new KinopoiskNativeTrailerBridgeResult
            {
                KinopoiskId = kinopoiskId,
                State = state,
                Registered = trailer is not null,
                MovieId = movie?.Id,
                MovieName = movie?.Name ?? string.Empty,
                TrailerId = trailer?.Id,
                LocalTrailerCount = movie?.LocalTrailers.Count ?? 0,
                PlaybackMode = trailer is null
                    ? string.Empty
                    : cached is null
                        ? "jellyfin-r7-server-transcode-fallback"
                        : "native-local-mp4-direct-play-with-r7-fallback",
                CacheEnabled = snapshot.Enabled,
                CacheReady = cached is not null,
                CachePath = cached?.Path ?? string.Empty,
                CacheEntryBytes = cached?.ContentLength ?? 0,
                CacheCurrentBytes = snapshot.CurrentBytes,
                CacheMaximumBytes = snapshot.MaximumBytes,
                CacheRetentionDays = snapshot.RetentionDays
            };
        }
    }
}
