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
    /// Выполнена регистрация визуальной локализации тегов через JavaScript Injector.
    /// </summary>
    public sealed class KinopoiskWebTagLocalizationService : IHostedService
    {
        private const string JavaScriptInjectorAssemblyName = "Jellyfin.Plugin.JavaScriptInjector";
        private const string JavaScriptInjectorInterfaceName =
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
        private const string ResourceName =
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskTagLocalization.js";
        private const string RegistrationSuffix = "kinopoisk-tag-localization";

        private readonly ILogger<KinopoiskWebTagLocalizationService> _logger;

        public KinopoiskWebTagLocalizationService(
            ILogger<KinopoiskWebTagLocalizationService> logger)
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
                        "web-tags.integration.unavailable",
                        "Локализация тегов не зарегистрирована: JavaScript Injector не найден.",
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
                        "web-tags.integration.unavailable",
                        "Локализация тегов не зарегистрирована: метод RegisterScript не найден.",
                        "RegisterScriptNotFound");
                    return;
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "Экземпляр плагина КиноПоиск ещё не создан.");
                var payload = new JObject
                {
                    ["id"] = $"{plugin.Id:D}-{RegistrationSuffix}",
                    ["name"] = "Локализация тегов КиноПоиска",
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
                        "web-tags.integration.failed",
                        "JavaScript Injector отклонил регистрацию локализации тегов.",
                        "RegisterScriptReturnedFalse");
                    return;
                }

                RecordStatus(
                    LogLevel.Information,
                    "web-tags.integration.registered",
                    "Визуальная локализация тегов зарегистрирована через JavaScript Injector.",
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
            var assembly = typeof(KinopoiskWebTagLocalizationService).Assembly;
            using var stream = assembly.GetManifestResourceStream(ResourceName)
                ?? throw new InvalidOperationException(
                    $"Встроенный ресурс локализации тегов не найден: {ResourceName}");
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
                "Регистрация локализации тегов завершилась ошибкой");
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Error,
                GetType().FullName ?? nameof(KinopoiskWebTagLocalizationService),
                "web-tags.integration.failed",
                "Регистрация локализации тегов завершилась ошибкой.",
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
                GetType().FullName ?? nameof(KinopoiskWebTagLocalizationService),
                eventName,
                message,
                fields);
        }
    }
}
