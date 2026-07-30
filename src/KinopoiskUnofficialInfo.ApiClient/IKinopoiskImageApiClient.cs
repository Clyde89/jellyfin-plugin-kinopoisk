using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет доступ к изображениям КиноПоиска.
    /// </summary>
    public interface IKinopoiskImageApiClient
    {
        Task<ImageResponse> GetImages(
            int filmId,
            FilmImageType type,
            int page = 1,
            CancellationToken? cancellationToken = null);
    }
}
