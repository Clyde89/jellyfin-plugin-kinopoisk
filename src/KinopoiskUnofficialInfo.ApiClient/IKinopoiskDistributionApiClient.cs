using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет прокатные даты фильма или сериала.
    /// </summary>
    public interface IKinopoiskDistributionApiClient
    {
        /// <summary>
        /// Получает сведения о прокате объекта КиноПоиска.
        /// </summary>
        /// <param name="filmId">Идентификатор КиноПоиска.</param>
        /// <param name="cancellationToken">Токен отмены операции.</param>
        /// <returns>Прокатные даты и компании.</returns>
        Task<DistributionResponse> GetDistributions(
            int filmId,
            CancellationToken? cancellationToken = null);
    }
}
