using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public interface IFilteredKinopoiskApiClient : IKinopoiskApiClient
    {
        Task<FilteredFilmSearchResponse> SearchFilms(
            FilmSearchQuery query,
            CancellationToken? cancellationToken = null);
    }
}
