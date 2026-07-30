using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет поиск персон КиноПоиска по имени.
    /// </summary>
    public interface IKinopoiskPersonSearchApiClient
    {
        Task<PersonSearchResponse> SearchPersons(
            string name,
            int page = 1,
            CancellationToken? cancellationToken = null);
    }
}
