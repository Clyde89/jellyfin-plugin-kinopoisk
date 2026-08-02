using System.Threading;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Хранит состояние клиентской интеграции трейлеров КиноПоиска.
    /// </summary>
    public static class KinopoiskWebTrailerIntegrationState
    {
        private static int _isRegistered;

        /// <summary>
        /// Получает признак успешной регистрации веб-плеера через JavaScript Injector.
        /// </summary>
        public static bool IsRegistered
            => Volatile.Read(ref _isRegistered) == 1;

        /// <summary>
        /// Обновляет состояние регистрации веб-плеера.
        /// </summary>
        /// <param name="isRegistered">Новое состояние.</param>
        public static void SetRegistered(bool isRegistered)
            => Interlocked.Exchange(ref _isRegistered, isRegistered ? 1 : 0);
    }
}
