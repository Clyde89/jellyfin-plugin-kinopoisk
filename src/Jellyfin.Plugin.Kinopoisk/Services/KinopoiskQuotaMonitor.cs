using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Model.Plugins;
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
        private readonly object _quotaUpdateSync = new();
        private PluginConfiguration _lastConfiguration;
        private TaskCompletionSource<bool> _configurationChanged = NewSignal();

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
            var plugin = Plugin.Instance;
            _lastConfiguration = plugin.Configuration;
            plugin.ConfigurationChanged += OnConfigurationChanged;
            try
            {
                while (!stoppingToken.IsCancellationRequested)
                {
                    var configurationChanged = Volatile.Read(ref _configurationChanged).Task;
                    var configuration = Plugin.Instance?.Configuration;
                    if (configuration is null
                        || !configuration.EnableQuotaMonitoring
                        || string.IsNullOrWhiteSpace(configuration.ApiToken))
                    {
                        await WaitForNextCheck(configurationChanged, DisabledCheckInterval, stoppingToken)
                            .ConfigureAwait(false);
                        continue;
                    }

                    try
                    {
                        var client = _serviceProvider.GetRequiredService<IKinopoiskQuotaApiClient>();
                        _diagnostics.RecordApiRequest();
                        var quota = await client.GetApiQuota(stoppingToken).ConfigureAwait(false);
                        lock (_quotaUpdateSync)
                        {
                            if (!string.Equals(configuration.ApiToken, plugin.Configuration.ApiToken, StringComparison.Ordinal))
                                continue;
                            _diagnostics.UpdateQuota(quota);
                        }

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
                    await WaitForNextCheck(configurationChanged, TimeSpan.FromHours(intervalHours), stoppingToken)
                        .ConfigureAwait(false);
                }
            }
            finally
            {
                plugin.ConfigurationChanged -= OnConfigurationChanged;
            }
        }

        private void OnConfigurationChanged(object sender, BasePluginConfiguration configuration)
        {
            if (configuration is not PluginConfiguration current)
                return;

            var previous = Interlocked.Exchange(ref _lastConfiguration, current);
            var tokenChanged = !string.Equals(previous.ApiToken, current.ApiToken, StringComparison.Ordinal);
            if (tokenChanged)
            {
                lock (_quotaUpdateSync)
                    _diagnostics.ResetQuota();
            }

            if (tokenChanged || previous.EnableQuotaMonitoring != current.EnableQuotaMonitoring
                || previous.QuotaCheckIntervalHours != current.QuotaCheckIntervalHours)
            {
                Interlocked.Exchange(ref _configurationChanged, NewSignal()).TrySetResult(true);
            }
        }

        private static TaskCompletionSource<bool> NewSignal()
            => new(TaskCreationOptions.RunContinuationsAsynchronously);

        private static async Task WaitForNextCheck(Task signal, TimeSpan delay, CancellationToken token)
        {
            try
            {
                await signal.WaitAsync(delay, token).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }
    }
}
