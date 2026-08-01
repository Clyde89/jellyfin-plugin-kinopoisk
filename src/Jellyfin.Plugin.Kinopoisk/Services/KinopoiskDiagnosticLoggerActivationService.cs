using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    public sealed class KinopoiskDiagnosticLoggerActivationService : IHostedService
    {
        private readonly ILoggerFactory _loggerFactory;
        private readonly KinopoiskDiagnosticLoggerProvider _provider;
        private bool _activated;

        public KinopoiskDiagnosticLoggerActivationService(
            ILoggerFactory loggerFactory,
            KinopoiskDiagnosticLoggerProvider provider)
        {
            _loggerFactory = loggerFactory;
            _provider = provider;
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            if (!_activated)
            {
                _loggerFactory.AddProvider(_provider);
                _activated = true;
            }

            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;
    }
}
