using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет связи фильма с сиквелами, приквелами и ремейками.
    /// </summary>
    public interface IKinopoiskRelationsApiClient
    {
        /// <summary>
        /// Получает связи фильма по идентификатору КиноПоиска.
        /// </summary>
        /// <param name="filmId">Идентификатор фильма КиноПоиска.</param>
        /// <param name="cancellationToken">Токен отмены.</param>
        /// <returns>Коллекция связанных фильмов.</returns>
        Task<ICollection<FilmSequelsAndPrequelsResponse>> GetRelations(
            int filmId,
            CancellationToken? cancellationToken = null);
    }
}
