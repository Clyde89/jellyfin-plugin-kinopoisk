using System.Threading;
using System.Threading.Tasks;

namespace KinopoiskUnofficialInfo.ApiClient
{
    /// <summary>
    /// Предоставляет сведения о состоянии API-ключа и квоты.
    /// </summary>
    public interface IKinopoiskQuotaApiClient
    {
        /// <summary>
        /// Получает текущее состояние квоты API-ключа.
        /// </summary>
        /// <param name="cancellationToken">Токен отмены операции.</param>
        /// <returns>Состояние учётной записи и квоты.</returns>
        Task<KinopoiskApiQuota> GetApiQuota(CancellationToken? cancellationToken = null);
    }

    /// <summary>
    /// Описывает состояние API-ключа.
    /// </summary>
    public sealed class KinopoiskApiQuota
    {
        /// <summary>
        /// Получает или задаёт тип учётной записи.
        /// </summary>
        public string AccountType { get; set; } = string.Empty;

        /// <summary>
        /// Получает или задаёт общую квоту.
        /// </summary>
        public KinopoiskApiQuotaLimit TotalQuota { get; set; } = new();

        /// <summary>
        /// Получает или задаёт суточную квоту.
        /// </summary>
        public KinopoiskApiQuotaLimit DailyQuota { get; set; } = new();
    }

    /// <summary>
    /// Описывает использованное и доступное количество запросов.
    /// </summary>
    public sealed class KinopoiskApiQuotaLimit
    {
        /// <summary>
        /// Получает или задаёт доступное количество запросов.
        /// </summary>
        public long Value { get; set; }

        /// <summary>
        /// Получает или задаёт использованное количество запросов.
        /// </summary>
        public long Used { get; set; }
    }
}
