#nullable enable

using System;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлен startup-фильтр Runtime Web Bootstrap КиноПоиска.
    /// </summary>
    internal sealed class KinopoiskWebBootstrapStartupFilter : IStartupFilter
    {
        private readonly KinopoiskWebBootstrapState _state;
        private readonly ILogger<KinopoiskWebBootstrapStartupFilter> _logger;

        public KinopoiskWebBootstrapStartupFilter(
            KinopoiskWebBootstrapState state,
            ILogger<KinopoiskWebBootstrapStartupFilter> logger)
        {
            _state = state ?? throw new ArgumentNullException(nameof(state));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
        {
            ArgumentNullException.ThrowIfNull(next);

            return applicationBuilder =>
            {
                applicationBuilder.UseMiddleware<KinopoiskWebBootstrapMiddleware>();
                _state.MarkPipelineRegistered();
                _logger.LogInformation(
                    "Runtime Web Bootstrap КиноПоиска зарегистрирован в HTTP pipeline Jellyfin.");
                next(applicationBuilder);
            };
        }
    }
}
