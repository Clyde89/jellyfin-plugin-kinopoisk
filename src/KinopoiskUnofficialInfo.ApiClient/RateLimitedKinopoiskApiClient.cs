using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    public sealed class RateLimitedKinopoiskApiClient : IFilteredKinopoiskApiClient
    {
        private static readonly TimeSpan MinimumRequestInterval = TimeSpan.FromMilliseconds(200);

        private readonly IKinopoiskApiClient _innerClient;
        private readonly IFilteredKinopoiskApiClient _filteredInnerClient;
        private readonly SemaphoreSlim _requestGate = new(1, 1);
        private DateTimeOffset _nextRequestAt = DateTimeOffset.MinValue;

        public RateLimitedKinopoiskApiClient(IKinopoiskApiClient innerClient)
        {
            _innerClient = innerClient ?? throw new ArgumentNullException(nameof(innerClient));
            _filteredInnerClient = innerClient as IFilteredKinopoiskApiClient;
        }

        public Task<PersonResponse> GetPerson(
            int personId,
            CancellationToken? cancellationToken = null)
        {
            return Invoke(
                token => _innerClient.GetPerson(personId, token),
                cancellationToken);
        }

        public Task<Film> GetSingleFilm(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            return Invoke(
                token => _innerClient.GetSingleFilm(filmId, token),
                cancellationToken);
        }

        public Task<ICollection<StaffResponse>> GetStaff(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            return Invoke(
                token => _innerClient.GetStaff(filmId, token),
                cancellationToken);
        }

        public Task<VideoResponse> GetTrailers(
            int filmId,
            CancellationToken? cancellationToken = null)
        {
            return Invoke(
                token => _innerClient.GetTrailers(filmId, token),
                cancellationToken);
        }

        public Task<FilmSearchResponse> SearchByKeyword(
            string keyword,
            int page = 1,
            CancellationToken? cancellationToken = null)
        {
            return Invoke(
                token => _innerClient.SearchByKeyword(keyword, page, token),
                cancellationToken);
        }

        public Task<FilteredFilmSearchResponse> SearchFilms(
            FilmSearchQuery query,
            CancellationToken? cancellationToken = null)
        {
            if (_filteredInnerClient is null)
                throw new NotSupportedException("Клиент КиноПоиска не поддерживает фильтрованный поиск.");

            return Invoke(
                token => _filteredInnerClient.SearchFilms(query, token),
                cancellationToken);
        }

        private async Task<T> Invoke<T>(
            Func<CancellationToken, Task<T>> request,
            CancellationToken? cancellationToken)
        {
            var callerCancellationToken = cancellationToken ?? CancellationToken.None;
            callerCancellationToken.ThrowIfCancellationRequested();

            await WaitForRequestWindow(callerCancellationToken).ConfigureAwait(false);
            return await request(callerCancellationToken).ConfigureAwait(false);
        }

        private async Task WaitForRequestWindow(CancellationToken cancellationToken)
        {
            await _requestGate.WaitAsync(cancellationToken).ConfigureAwait(false);

            try
            {
                var delay = _nextRequestAt - DateTimeOffset.UtcNow;
                if (delay > TimeSpan.Zero)
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);

                _nextRequestAt = DateTimeOffset.UtcNow + MinimumRequestInterval;
            }
            finally
            {
                _requestGate.Release();
            }
        }
    }
}
