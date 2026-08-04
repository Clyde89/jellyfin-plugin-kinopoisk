#nullable enable

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
    /// Выполнена регистрация объединённых рекомендаций через JavaScript Injector.
    /// </summary>
    public sealed class KinopoiskWebRecommendationsService : IHostedService
    {
        private const string JavaScriptInjectorAssemblyName = "Jellyfin.Plugin.JavaScriptInjector";
        private const string JavaScriptInjectorInterfaceName =
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
        private const string ResourceName =
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendations.js";
        private const string RegistrationSuffix = "kinopoisk-recommendations";

        private readonly ILogger<KinopoiskWebRecommendationsService> _logger;

        public KinopoiskWebRecommendationsService(
            ILogger<KinopoiskWebRecommendationsService> logger)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RegisterScript();
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        private void RegisterScript()
        {
            try
            {
                var injectorAssembly = AssemblyLoadContext.All
                    .SelectMany(context => context.Assemblies)
                    .FirstOrDefault(assembly =>
                        assembly.GetName().Name?.Contains(
                            JavaScriptInjectorAssemblyName,
                            StringComparison.OrdinalIgnoreCase) == true);
                if (injectorAssembly is null)
                {
                    RecordStatus(
                        LogLevel.Warning,
                        "web-recommendations.integration.unavailable",
                        "Объединённые рекомендации не зарегистрированы: JavaScript Injector не найден.",
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
                        "web-recommendations.integration.unavailable",
                        "Объединённые рекомендации не зарегистрированы: метод RegisterScript не найден.",
                        "RegisterScriptNotFound");
                    return;
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "Экземпляр плагина КиноПоиск ещё не создан.");
                var payload = new JObject
                {
                    ["id"] = $"{plugin.Id:D}-{RegistrationSuffix}",
                    ["name"] = "Похожие и рекомендации КиноПоиска",
                    ["script"] = ReadEmbeddedScript(),
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
                        "web-recommendations.integration.failed",
                        "JavaScript Injector отклонил регистрацию объединённых рекомендаций.",
                        "RegisterScriptReturnedFalse");
                    return;
                }

                RecordStatus(
                    LogLevel.Information,
                    "web-recommendations.integration.registered",
                    "Объединённые рекомендации зарегистрированы через JavaScript Injector.",
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

        private static string ReadEmbeddedScript()
        {
            var assembly = typeof(KinopoiskWebRecommendationsService).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Встроенный ресурс объединённых рекомендаций не найден: {ResourceName}");
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
                "Регистрация объединённых рекомендаций завершилась ошибкой");
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Error,
                GetType().FullName ?? nameof(KinopoiskWebRecommendationsService),
                "web-recommendations.integration.failed",
                "Регистрация объединённых рекомендаций завершилась ошибкой.",
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

            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            if (!string.IsNullOrWhiteSpace(reason))
                fields["reason"] = reason;

            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                level,
                GetType().FullName ?? nameof(KinopoiskWebRecommendationsService),
                eventName,
                message,
                fields);
        }
    }
}
