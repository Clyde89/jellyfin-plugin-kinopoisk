using System;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Периодически обновляет состояние API-ключа без раскрытия токена.
    /// </summary>
    public sealed class KinopoiskQuotaMonitor : BackgroundService
    {
        private static readonly TimeSpan DisabledCheckInterval = TimeSpan.FromMinutes(5);
        private readonly IServiceProvider _serviceProvider;
        private readonly KinopoiskDiagnostics _diagnostics;
        private readonly ILogger<KinopoiskQuotaMonitor> _logger;

        public KinopoiskQuotaMonitor(
            IServiceProvider serviceProvider,
            KinopoiskDiagnostics diagnostics,
            ILogger<KinopoiskQuotaMonitor> logger)
        {
            _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
            _diagnostics = diagnostics ?? throw new ArgumentNullException(nameof(diagnostics));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var configuration = Plugin.Instance?.Configuration;
                if (configuration is null
                    || !configuration.EnableQuotaMonitoring
                    || string.IsNullOrWhiteSpace(configuration.ApiToken))
                {
                    await Task.Delay(DisabledCheckInterval, stoppingToken).ConfigureAwait(false);
                    continue;
                }

                try
                {
                    var client = _serviceProvider.GetRequiredService<IKinopoiskQuotaApiClient>();
                    _diagnostics.RecordApiRequest();
                    var quota = await client.GetApiQuota(stoppingToken).ConfigureAwait(false);
                    _diagnostics.UpdateQuota(quota);

                    _logger.LogInformation(
                        "Состояние квоты КиноПоиска обновлено: суточно {DailyUsed}/{DailyValue}, всего {TotalUsed}/{TotalValue}",
                        quota.DailyQuota?.Used ?? 0,
                        quota.DailyQuota?.Value ?? 0,
                        quota.TotalQuota?.Used ?? 0,
                        quota.TotalQuota?.Value ?? 0);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception exception)
                {
                    _diagnostics.RecordApiFailure();
                    _diagnostics.RecordQuotaFailure();
                    _logger.LogWarning(exception, "Состояние квоты КиноПоиска не обновлено");
                }

                var intervalHours = Math.Clamp(configuration.QuotaCheckIntervalHours, 1, 168);
                await Task.Delay(TimeSpan.FromHours(intervalHours), stoppingToken).ConfigureAwait(false);
            }
        }
    }
}
