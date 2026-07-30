using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет сведения о сезонах и эпизодах сериала.
    /// </summary>
    public interface IKinopoiskSeasonApiClient
    {
        Task<SeasonResponse> GetSeasons(
            int filmId,
            CancellationToken? cancellationToken = null);
    }
}
