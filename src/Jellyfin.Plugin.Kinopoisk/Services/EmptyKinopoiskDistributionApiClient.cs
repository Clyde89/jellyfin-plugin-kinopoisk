using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Сохраняет совместимость конструкторов при отсутствии поставщика прокатных дат.
    /// </summary>
    internal sealed class EmptyKinopoiskDistributionApiClient : IKinopoiskDistributionApiClient
    {
        /// <summary>
        /// Получает общий экземпляр пустого поставщика.
        /// </summary>
        public static EmptyKinopoiskDistributionApiClient Instance { get; } = new();

        private EmptyKinopoiskDistributionApiClient()
        {
        }

        /// <inheritdoc />
        public Task<DistributionResponse> GetDistributions(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            cancellationToken?.ThrowIfCancellationRequested();
            return Task.FromResult(new DistributionResponse());
        }
    }
}
