#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using KinopoiskUnofficialInfo.ApiClient;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Подключает автономный веб-клиент КиноПоиска без обязательного JavaScript-инжектора.
    /// </summary>
    public sealed class KinopoiskStandaloneWebClientService : IHostedService
    {
        internal const string BeginMarker = "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->";
        internal const string EndMarker = "<!-- KINOPOISK_WEB_CLIENT_END -->";
        internal const string WebClientPath = "../Kinopoisk/WebClient.js";
        internal const string ExternalManagedAttribute =
            "data-kinopoisk-managed=\"external\"";

        private const string JavaScriptInjectorAssemblyName =
            "Jellyfin.Plugin.JavaScriptInjector";
        private const string JavaScriptInjectorInterfaceName =
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
        private const string BackupDirectoryName = "web-client-backup";
        private const string InitialIndexBackupName = "index.html.before-kinopoisk";
        private const string RollbackScriptName = "rollback.sh";
        private const string ManifestFileName = "manifest.json";

        private readonly IApplicationPaths _applicationPaths;
        private readonly ILogger<KinopoiskStandaloneWebClientService> _logger;

        public KinopoiskStandaloneWebClientService(
            IApplicationPaths applicationPaths,
            ILogger<KinopoiskStandaloneWebClientService> logger)
        {
            _applicationPaths = applicationPaths
                ?? throw new ArgumentNullException(nameof(applicationPaths));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <inheritdoc />
        public Task StartAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            InstallWebClient();
            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task StopAsync(CancellationToken cancellationToken)
            => Task.CompletedTask;

        private void InstallWebClient()
        {
            KinopoiskWebTrailerIntegrationState.SetRegistered(false);

            try
            {
                var webPath = _applicationPaths.WebPath;
                if (string.IsNullOrWhiteSpace(webPath) || !Directory.Exists(webPath))
                {
                    RecordFailure("WebPathUnavailable", "Каталог Jellyfin Web не найден.");
                    return;
                }

                var indexPath = Path.Combine(webPath, "index.html");
                if (!File.Exists(indexPath))
                {
                    RecordFailure("IndexNotFound", "Файл index.html Jellyfin Web не найден.");
                    return;
                }

                var plugin = Plugin.Instance
                    ?? throw new InvalidOperationException(
                        "Экземпляр плагина КиноПоиск ещё не создан.");
                var digest = KinopoiskWebClientBundle.Sha256;
                var originalIndex = File.ReadAllText(indexPath, Encoding.UTF8);

                if (IsExternallyManagedIndex(originalIndex))
                {
                    VerifyExternalManagedIndex(originalIndex);
                    CompleteRegistration(
                        plugin,
                        digest,
                        changed: false,
                        backupCreated: false,
                        managedExternally: true);
                    return;
                }

                var cleanIndex = RemoveManagedBlock(originalIndex);
                var updatedIndex = BuildManagedIndex(cleanIndex, digest);
                var indexMatches = string.Equals(
                    originalIndex,
                    updatedIndex,
                    StringComparison.Ordinal);
                string? backupDirectory = null;

                if (!indexMatches)
                {
                    CreateInitialBackup(plugin.DataFolderPath, cleanIndex);
                    backupDirectory = CreateTransactionBackup(
                        plugin.DataFolderPath,
                        indexPath,
                        originalIndex,
                        digest);

                    try
                    {
                        WriteTextAtomically(indexPath, updatedIndex);
                        VerifyInstallation(indexPath, updatedIndex, digest);
                    }
                    catch (Exception exception)
                    {
                        TryRestorePreviousIndex(indexPath, originalIndex);
                        if (exception is UnauthorizedAccessException or IOException)
                        {
                            RecordWriteUnavailable(exception, indexPath);
                            return;
                        }

                        throw;
                    }
                }
                else
                {
                    VerifyInstallation(indexPath, updatedIndex, digest);
                }

                CompleteRegistration(
                    plugin,
                    digest,
                    changed: !indexMatches,
                    backupCreated: backupDirectory is not null,
                    managedExternally: false);
            }
            catch (Exception exception)
            {
                KinopoiskWebTrailerIntegrationState.SetRegistered(false);
                _logger.LogError(exception, "Автономный веб-клиент КиноПоиска не подключён");
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Error,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.failed",
                    "Автономный веб-клиент КиноПоиска не подключён.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["errorType"] = exception.GetType().Name
                    });
            }
        }

        internal static string BuildManagedIndex(string value, string digest)
        {
            if (string.IsNullOrWhiteSpace(digest) || digest.Length < 16)
                throw new ArgumentException("Некорректная контрольная сумма веб-клиента.", nameof(digest));

            return InsertManagedBlock(
                value,
                $"    <script src=\"{WebClientPath}?v={digest[..16]}\" defer></script>");
        }

        internal static string BuildExternallyManagedIndex(string value)
            => InsertManagedBlock(
                value,
                $"    <script src=\"{WebClientPath}\" defer {ExternalManagedAttribute}></script>");

        internal static bool IsExternallyManagedIndex(string value)
            => CountOccurrences(value, BeginMarker) == 1
                && CountOccurrences(value, EndMarker) == 1
                && CountOccurrences(value, WebClientPath) == 1
                && CountOccurrences(value, ExternalManagedAttribute) == 1;

        internal static string RemoveManagedBlock(string value)
        {
            var start = value.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (start < 0)
                return value;

            var end = value.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < 0)
            {
                throw new InvalidDataException(
                    "Начальный маркер веб-клиента найден без конечного маркера.");
            }

            end += EndMarker.Length;
            while (end < value.Length && (value[end] == '\r' || value[end] == '\n'))
                end++;
            return value.Remove(start, end - start);
        }

        private static string InsertManagedBlock(string value, string scriptElement)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Содержимое index.html не задано.", nameof(value));

            var cleanIndex = RemoveManagedBlock(value);
            var bodyEnd = cleanIndex.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < 0)
                throw new InvalidDataException("Закрывающий тег body в index.html не найден.");

            var scriptBlock = string.Join(
                Environment.NewLine,
                BeginMarker,
                scriptElement,
                EndMarker);
            return cleanIndex.Insert(bodyEnd, scriptBlock + Environment.NewLine);
        }

        private static void VerifyInstallation(
            string indexPath,
            string expectedIndex,
            string expectedDigest)
        {
            var actualIndex = File.ReadAllText(indexPath, Encoding.UTF8);
            if (!string.Equals(actualIndex, expectedIndex, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "index.html не совпал с ожидаемым содержимым после подключения веб-клиента.");
            }

            if (CountOccurrences(actualIndex, BeginMarker) != 1
                || CountOccurrences(actualIndex, EndMarker) != 1
                || CountOccurrences(actualIndex, WebClientPath) != 1
                || !actualIndex.Contains(
                    string.Concat("?v=", expectedDigest[..16]),
                    StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Управляемый блок автономного веб-клиента установлен некорректно.");
            }
        }

        private static void VerifyExternalManagedIndex(string value)
        {
            if (!IsExternallyManagedIndex(value))
            {
                throw new InvalidDataException(
                    "Внешне управляемый блок автономного веб-клиента установлен некорректно.");
            }

            var markerStart = value.IndexOf(BeginMarker, StringComparison.Ordinal);
            var markerEnd = value.IndexOf(EndMarker, markerStart, StringComparison.Ordinal);
            var scriptStart = value.IndexOf(WebClientPath, markerStart, StringComparison.Ordinal);
            var attributeStart = value.IndexOf(
                ExternalManagedAttribute,
                markerStart,
                StringComparison.Ordinal);
            if (scriptStart < markerStart
                || scriptStart > markerEnd
                || attributeStart < markerStart
                || attributeStart > markerEnd)
            {
                throw new InvalidDataException(
                    "Endpoint или признак внешнего управления находятся вне управляемого блока.");
            }
        }

        private static int CountOccurrences(string value, string marker)
        {
            var count = 0;
            var position = 0;
            while ((position = value.IndexOf(marker, position, StringComparison.Ordinal)) >= 0)
            {
                count++;
                position += marker.Length;
            }

            return count;
        }

        private void CompleteRegistration(
            Plugin plugin,
            string digest,
            bool changed,
            bool backupCreated,
            bool managedExternally)
        {
            var removedLegacyRegistrations =
                TryRemoveLegacyInjectorRegistrations(plugin);
            KinopoiskWebTrailerIntegrationState.SetRegistered(true);

            _logger.LogInformation(
                "Автономный веб-клиент КиноПоиска готов; endpoint {Endpoint}; "
                + "SHA-256 {Digest}; внешнее управление index.html: {ManagedExternally}; "
                + "удалено старых регистраций Injector: {RemovedCount}",
                WebClientPath,
                digest,
                managedExternally,
                removedLegacyRegistrations);
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Information,
                GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                "web-client.standalone.installed",
                "Автономный веб-клиент КиноПоиска подключён и проверен.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["digest"] = digest,
                    ["resourceCount"] = KinopoiskWebClientBundle.ResourceCount
                        .ToString(CultureInfo.InvariantCulture),
                    ["delivery"] = "plugin-api",
                    ["endpoint"] = WebClientPath,
                    ["changed"] = changed.ToString(),
                    ["managedExternally"] = managedExternally.ToString(),
                    ["legacyRegistrationsRemoved"] =
                        removedLegacyRegistrations.ToString(CultureInfo.InvariantCulture),
                    ["backupCreated"] = backupCreated.ToString()
                });
        }

        private static void CreateInitialBackup(string dataFolderPath, string cleanIndex)
        {
            var backupDirectory = Path.Combine(dataFolderPath, BackupDirectoryName);
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, InitialIndexBackupName);
            if (!File.Exists(backupPath))
                WriteTextAtomically(backupPath, cleanIndex);
        }

        private static string CreateTransactionBackup(
            string dataFolderPath,
            string indexPath,
            string originalIndex,
            string digest)
        {
            var backupRoot = Path.Combine(dataFolderPath, BackupDirectoryName);
            Directory.CreateDirectory(backupRoot);
            var transactionName = string.Concat(
                DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff", CultureInfo.InvariantCulture),
                "-",
                digest[..12]);
            var transactionDirectory = Path.Combine(backupRoot, transactionName);
            if (Directory.Exists(transactionDirectory))
            {
                transactionDirectory = Path.Combine(
                    backupRoot,
                    string.Concat(transactionName, "-", Guid.NewGuid().ToString("N")[..8]));
            }

            Directory.CreateDirectory(transactionDirectory);
            WriteTextAtomically(
                Path.Combine(transactionDirectory, "index.html"),
                originalIndex);

            var manifest = JsonSerializer.Serialize(
                new
                {
                    createdUtc = DateTimeOffset.UtcNow,
                    indexPath,
                    delivery = "plugin-api",
                    endpoint = WebClientPath,
                    targetDigest = digest
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            WriteTextAtomically(
                Path.Combine(transactionDirectory, ManifestFileName),
                manifest + Environment.NewLine);
            WriteRollbackScript(transactionDirectory, indexPath);
            WriteTextAtomically(
                Path.Combine(backupRoot, "LATEST"),
                transactionDirectory + Environment.NewLine);
            return transactionDirectory;
        }

        private static void WriteRollbackScript(
            string transactionDirectory,
            string indexPath)
        {
            var backupIndexPath = Path.Combine(transactionDirectory, "index.html");
            var builder = new StringBuilder();
            builder.AppendLine("#!/bin/sh");
            builder.AppendLine("set -eu");
            builder.Append("cp -- ")
                .Append(EscapeShellArgument(backupIndexPath))
                .Append(' ')
                .AppendLine(EscapeShellArgument(indexPath));
            builder.AppendLine("printf '%s\\n' 'Откат автономного веб-клиента КиноПоиска выполнен.'");
            var rollbackPath = Path.Combine(transactionDirectory, RollbackScriptName);
            WriteTextAtomically(rollbackPath, builder.ToString());

            if (!OperatingSystem.IsWindows())
            {
                try
                {
                    File.SetUnixFileMode(
                        rollbackPath,
                        UnixFileMode.UserRead
                        | UnixFileMode.UserWrite
                        | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead
                        | UnixFileMode.GroupExecute);
                }
                catch (PlatformNotSupportedException)
                {
                    // На платформе без Unix-режимов сценарий остаётся доступен для ручного запуска.
                }
            }
        }

        private static string EscapeShellArgument(string value)
            => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

        private void TryRestorePreviousIndex(string indexPath, string originalIndex)
        {
            try
            {
                if (File.Exists(indexPath)
                    && string.Equals(
                        File.ReadAllText(indexPath, Encoding.UTF8),
                        originalIndex,
                        StringComparison.Ordinal))
                {
                    return;
                }

                WriteTextAtomically(indexPath, originalIndex);
                _logger.LogWarning(
                    "Неуспешное подключение автономного веб-клиента КиноПоиска автоматически отменено");
            }
            catch (Exception rollbackException)
            {
                _logger.LogCritical(
                    rollbackException,
                    "Автоматический откат index.html КиноПоиска завершился ошибкой");
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Critical,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.rollback-failed",
                    "Автоматический откат index.html КиноПоиска завершился ошибкой.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["errorType"] = rollbackException.GetType().Name
                    });
            }
        }

        private int TryRemoveLegacyInjectorRegistrations(Plugin plugin)
        {
            try
            {
                var injectorAssembly = AssemblyLoadContext.All
                    .SelectMany(context => context.Assemblies)
                    .FirstOrDefault(assembly => string.Equals(
                        assembly.GetName().Name,
                        JavaScriptInjectorAssemblyName,
                        StringComparison.OrdinalIgnoreCase));
                if (injectorAssembly is null)
                    return 0;

                var interfaceType = injectorAssembly.GetType(JavaScriptInjectorInterfaceName);
                var unregisterMethod = interfaceType?.GetMethod(
                    "UnregisterAllScriptsFromPlugin",
                    BindingFlags.Public | BindingFlags.Static,
                    binder: null,
                    types: new[] { typeof(string) },
                    modifiers: null);
                if (unregisterMethod is null)
                {
                    _logger.LogWarning(
                        "JavaScript Injector найден, но метод удаления старых регистраций недоступен");
                    return 0;
                }

                var result = unregisterMethod.Invoke(
                    null,
                    new object[] { plugin.Id.ToString("D") });
                return result is int removedCount ? removedCount : 0;
            }
            catch (TargetInvocationException exception)
            {
                RecordLegacyCleanupFailure(exception.InnerException ?? exception);
                return 0;
            }
            catch (Exception exception)
            {
                RecordLegacyCleanupFailure(exception);
                return 0;
            }
        }

        private void RecordLegacyCleanupFailure(Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Старые регистрации КиноПоиска в JavaScript Injector не удалены; "
                + "автономный веб-клиент продолжает работать независимо");
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Warning,
                GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                "web-client.standalone.legacy-cleanup-failed",
                "Старые регистрации КиноПоиска в JavaScript Injector не удалены.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorType"] = exception.GetType().Name
                });
        }

        private static void WriteTextAtomically(string path, string content)
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Родительский каталог не определён: {path}");
            Directory.CreateDirectory(directory);
            UnixFileMode? previousMode = null;
            if (!OperatingSystem.IsWindows() && File.Exists(path))
            {
                try
                {
                    previousMode = File.GetUnixFileMode(path);
                }
                catch (PlatformNotSupportedException)
                {
                    previousMode = null;
                }
            }

            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
                File.Move(temporaryPath, path, true);
                if (previousMode.HasValue)
                    File.SetUnixFileMode(path, previousMode.Value);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }

        private void RecordWriteUnavailable(Exception exception, string indexPath)
        {
            KinopoiskWebTrailerIntegrationState.SetRegistered(false);
            _logger.LogWarning(
                exception,
                "index.html Jellyfin Web недоступен для записи: {IndexPath}. "
                + "Для защищённого контейнера требуется read-only bind-монтирование внешне управляемого index.html",
                indexPath);
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Warning,
                GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                "web-client.standalone.index-write-unavailable",
                "index.html Jellyfin Web недоступен для записи; серверные функции КиноПоиска продолжают работать.",
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["errorType"] = exception.GetType().Name,
                    ["indexPath"] = indexPath,
                    ["requiredAction"] = "bind-mount-external-index-read-only"
                });
        }

        private void RecordFailure(string reason, string message)
        {
            KinopoiskWebTrailerIntegrationState.SetRegistered(false);
            _logger.LogWarning("{Message} Причина: {Reason}", message, reason);
            KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                LogLevel.Warning,
                GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                "web-client.standalone.unavailable",
                message,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["reason"] = reason
                });
        }
    }
}
