using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json.Linq;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Регистрирует клиентский веб-плеер виджетов КиноПоиска через JavaScript Injector.
    /// </summary>
    public sealed class KinopoiskWebTrailerIntegrationService : IHostedService
    {
        private const string JavaScriptInjectorAssemblyName = "Jellyfin.Plugin.JavaScriptInjector";
        private const string JavaScriptInjectorInterfaceName =
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
        private const string ResourceName =
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskWidgetTrailerPlayer.js";
        private const string RegistrationSuffix = "kinopoisk-widget-trailer-player";

        private readonly ILogger<KinopoiskWebTrailerIntegrationService> _logger;

        /// <summary>
        /// Инициализирует службу клиентской интеграции трейлеров.
        /// </summary>
        public KinopoiskWebTrailerIntegrationService(
            ILogger<KinopoiskWebTrailerIntegrationService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterScript();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        private void RegisterScript()
        {
            try
            {
                var injectorAssembly = FindJavaScriptInjectorAssembly();
                if (injectorAssembly is null)
                {
                    RecordStatus(
                        LogLevel.Warning,
                        "web-trailer.integration.unavailable",
                        "Веб-плеер трейлеров КиноПоиска не зарегистрирован: JavaScript Injector не найден.",
                        "JavaScriptInjectorNotFound");
                    return;
                }

                var interfaceType = injectorAssembly.GetType(JavaScriptInjectorInterfaceName);
                var registerMethod = interfaceType?.GetMethod(
                    "RegisterScript",
                    BindingFlags.Public | BindingFlags.Static);
                if (registerMethod is null)
                {
                    RecordStatus(
                        LogLevel.Warning,
                        "web-trailer.integration.unavailable",
                        "Веб-плеер трейлеров КиноПоиска не зарегистрирован: метод RegisterScript не найден.",
                        "RegisterScriptNotFound");
                    return;
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "Экземпляр плагина КиноПоиск ещё не создан.");
                var script = ReadEmbeddedScript();
                var registrationId = $"{plugin.Id:D}-{RegistrationSuffix}";
                var payload = new JObject
                {
                    ["id"] = registrationId,
                    ["name"] = "Веб-плеер трейлеров КиноПоиска",
                    ["script"] = script,
                    ["enabled"] = true,
                    ["requiresAuthentication"] = true,
                    ["pluginId"] = plugin.Id.ToString("D"),
                    ["pluginName"] = plugin.Name,
                    ["pluginVersion"] = plugin.Version.ToString()
                };

                var result = registerMethod.Invoke(null, new object[] { payload });
                if (result is not bool success || !success)
                {
                    RecordStatus(
                        LogLevel.Warning,
                        "web-trailer.integration.failed",
                        "JavaScript Injector отклонил регистрацию веб-плеера трейлеров КиноПоиска.",
                        "RegisterScriptReturnedFalse");
                    return;
                }

                RecordStatus(
                    LogLevel.Information,
                    "web-trailer.integration.registered",
                    "Веб-плеер трейлеров КиноПоиска зарегистрирован через JavaScript Injector.",
                    null);
            }
            catch (TargetInvocationException exception)
            {
                RecordFailure(exception.InnerException ?? exception);
            }
            catch (Exception exception)
            {
                RecordFailure(exception);
            }
        }

        private static Assembly? FindJavaScriptInjectorAssembly()
        {
            return AssemblyLoadContext.All
                .SelectMany(context => context.Assemblies)
                .FirstOrDefault(assembly =>
                    assembly.GetName().Name?.Contains(
                        JavaScriptInjectorAssemblyName,
                        StringComparison.OrdinalIgnoreCase) == true);
        }

        private static string ReadEmbeddedScript()
        {
            var assembly = typeof(KinopoiskWebTrailerIntegrationService).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Встроенный ресурс веб-плеера не найден: {ResourceName}");
            using var reader = new StreamReader(
                stream,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: true,
                leaveOpen: false);
            return reader.ReadToEnd();
        }

        private void RecordFailure(Exception exception)
        {
            _logger.LogError(
                exception,
                "Регистрация веб-плеера трейлеров КиноПоиска завершилась ошибкой");

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Error,
                GetType().FullName ?? nameof(KinopoiskWebTrailerIntegrationService),
                "web-trailer.integration.failed",
                "Регистрация веб-плеера трейлеров КиноПоиска завершилась ошибкой.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorType"] = exception.GetType().Name
                });
        }

        private void RecordStatus(
            LogLevel level,
            string eventName,
            string message,
            string? reason)
        {
            if (level == LogLevel.Information)
                _logger.LogInformation("{Message}", message);
            else
                _logger.LogWarning("{Message} Причина: {Reason}", message, reason);

            var fields = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["registered"] = (level == LogLevel.Information).ToString()
            };
            if (!string.IsNullOrWhiteSpace(reason))
                fields["reason"] = reason;

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                level,
                GetType().FullName ?? nameof(KinopoiskWebTrailerIntegrationService),
                eventName,
                message,
                fields);
        }
    }
}
