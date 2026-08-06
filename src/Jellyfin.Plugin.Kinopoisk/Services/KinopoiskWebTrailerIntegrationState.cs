using System.Threading;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Хранит состояние автономной клиентской интеграции КиноПоиска.
    /// </summary>
    public static class KinopoiskWebTrailerIntegrationState
    {
        private static int _isRegistered;

        /// <summary>
        /// Получает признак успешной установки и проверки автономного веб-клиента.
        /// </summary>
        public static bool IsRegistered
            => Volatile.Read(ref _isRegistered) == 1;

        /// <summary>
        /// Обновляет состояние автономного веб-клиента.
        /// </summary>
        /// <param name="isRegistered">Новое состояние.</param>
        public static void SetRegistered(bool isRegistered)
            => Interlocked.Exchange(ref _isRegistered, isRegistered ? 1 : 0);
    }
}
