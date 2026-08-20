#nullable enable

using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Управляет локальными MP4-копиями HLS-трейлеров КиноПоиска.
    /// </summary>
    public interface IKinopoiskNativeTrailerCache
    {
        bool Enabled { get; }

        string GetExpectedPath(int kinopoiskId);

        KinopoiskNativeTrailerCacheEntry? TryGet(int kinopoiskId, bool touch = true);

        void RecordHit();

        void RecordMiss();

        Task<KinopoiskNativeTrailerCacheEntry?> GetOrCreate(
            int kinopoiskId,
            KinopoiskTrailerPlaybackSource source,
            CancellationToken cancellationToken);

        Task<KinopoiskNativeTrailerCacheCleanupResult> Cleanup(
            CancellationToken cancellationToken);

        KinopoiskNativeTrailerCacheSnapshot GetSnapshot();
    }
}
