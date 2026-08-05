#nullable enable

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography;
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
    /// Устанавливает автономный веб-клиент КиноПоиска без обязательного внешнего JavaScript-инжектора.
    /// </summary>
    public sealed class KinopoiskStandaloneWebClientService : IHostedService
    {
        internal const string BeginMarker = "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->";
        internal const string EndMarker = "<!-- KINOPOISK_WEB_CLIENT_END -->";
        internal const string ClientFileName = "kinopoisk-web-client.js";

        private const string JavaScriptInjectorAssemblyName =
            "Jellyfin.Plugin.JavaScriptInjector";
        private const string JavaScriptInjectorInterfaceName =
            "Jellyfin.Plugin.JavaScriptInjector.PluginInterface";
        private const string BackupDirectoryName = "web-client-backup";
        private const string InitialIndexBackupName = "index.html.before-kinopoisk";
        private const string RollbackScriptName = "rollback.sh";
        private const string ManifestFileName = "manifest.json";

        private static readonly string[] ResourceNames =
        {
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskWidgetTrailerPlayer.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskEnhancedPresentation.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskReviewsIntegration.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskTagLocalization.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskElsewhereBridge.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendations.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimePolish.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskCarouselRebind.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeUiCorrections.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRuntimeNativeStyle.js",
            "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskRecommendationScrollbarStyle.js"
        };

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
                var script = ReadEmbeddedScripts();
                var digest = ComputeSha256(script);
                var clientPath = Path.Combine(webPath, ClientFileName);
                var originalIndex = File.ReadAllText(indexPath, Encoding.UTF8);
                var cleanIndex = RemoveManagedBlock(originalIndex);
                var updatedIndex = BuildManagedIndex(cleanIndex, digest);

                CreateInitialBackup(plugin.DataFolderPath, cleanIndex);

                var previousClientExists = File.Exists(clientPath);
                var previousClient = previousClientExists
                    ? File.ReadAllText(clientPath, Encoding.UTF8)
                    : null;
                var clientMatches = previousClientExists
                    && string.Equals(
                        ComputeFileSha256(clientPath),
                        digest,
                        StringComparison.Ordinal);
                var indexMatches = string.Equals(
                    originalIndex,
                    updatedIndex,
                    StringComparison.Ordinal);
                string? backupDirectory = null;

                if (!clientMatches || !indexMatches)
                {
                    backupDirectory = CreateTransactionBackup(
                        plugin.DataFolderPath,
                        indexPath,
                        originalIndex,
                        clientPath,
                        previousClientExists,
                        previousClient,
                        digest);

                    try
                    {
                        WriteTextAtomically(clientPath, script);
                        WriteTextAtomically(indexPath, updatedIndex);
                        VerifyInstallation(indexPath, updatedIndex, clientPath, digest);
                    }
                    catch
                    {
                        RestorePreviousInstallation(
                            indexPath,
                            originalIndex,
                            clientPath,
                            previousClientExists,
                            previousClient);
                        throw;
                    }
                }
                else
                {
                    VerifyInstallation(indexPath, updatedIndex, clientPath, digest);
                }

                var removedLegacyRegistrations =
                    TryRemoveLegacyInjectorRegistrations(plugin);
                KinopoiskWebTrailerIntegrationState.SetRegistered(true);

                _logger.LogInformation(
                    "Автономный веб-клиент КиноПоиска готов в {WebPath}; SHA-256 {Digest}; "
                    + "удалено старых регистраций Injector: {RemovedCount}",
                    webPath,
                    digest,
                    removedLegacyRegistrations);
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Information,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.installed",
                    "Автономный веб-клиент КиноПоиска установлен и проверен.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["digest"] = digest,
                        ["resourceCount"] = ResourceNames.Length.ToString(CultureInfo.InvariantCulture),
                        ["changed"] = (!clientMatches || !indexMatches).ToString(),
                        ["legacyRegistrationsRemoved"] =
                            removedLegacyRegistrations.ToString(CultureInfo.InvariantCulture),
                        ["backupCreated"] = (backupDirectory is not null).ToString()
                    });
            }
            catch (Exception exception)
            {
                KinopoiskWebTrailerIntegrationState.SetRegistered(false);
                _logger.LogError(exception, "Автономный веб-клиент КиноПоиска не установлен");
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Error,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.failed",
                    "Автономный веб-клиент КиноПоиска не установлен.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["errorType"] = exception.GetType().Name
                    });
            }
        }

        internal static string BuildManagedIndex(string value, string digest)
        {
            if (string.IsNullOrWhiteSpace(value))
                throw new ArgumentException("Содержимое index.html не задано.", nameof(value));
            if (string.IsNullOrWhiteSpace(digest) || digest.Length < 16)
                throw new ArgumentException("Некорректная контрольная сумма веб-клиента.", nameof(digest));

            var cleanIndex = RemoveManagedBlock(value);
            var bodyEnd = cleanIndex.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (bodyEnd < 0)
                throw new InvalidDataException("Закрывающий тег body в index.html не найден.");

            var scriptBlock = string.Join(
                Environment.NewLine,
                BeginMarker,
                $"    <script src=\"{ClientFileName}?v={digest[..16]}\" defer></script>",
                EndMarker);
            return cleanIndex.Insert(bodyEnd, scriptBlock + Environment.NewLine);
        }

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

        internal static string ComputeSha256(string value)
            => Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();

        private static string ComputeFileSha256(string path)
            => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant();

        private static string ReadEmbeddedScripts()
        {
            var assembly = typeof(KinopoiskStandaloneWebClientService).Assembly;
            var builder = new StringBuilder();
            foreach (var resourceName in ResourceNames)
            {
                using var stream = assembly.GetManifestResourceStream(resourceName)
                    ?? throw new InvalidOperationException(
                        $"Встроенный ресурс веб-клиента не найден: {resourceName}");
                using var reader = new StreamReader(
                    stream,
                    Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true,
                    leaveOpen: false);
                if (builder.Length > 0)
                    builder.AppendLine().AppendLine();
                builder.Append(reader.ReadToEnd());
            }

            return builder.ToString();
        }

        private static void VerifyInstallation(
            string indexPath,
            string expectedIndex,
            string clientPath,
            string expectedDigest)
        {
            if (!File.Exists(clientPath))
                throw new InvalidDataException("Файл автономного веб-клиента не создан.");

            var actualDigest = ComputeFileSha256(clientPath);
            if (!string.Equals(actualDigest, expectedDigest, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "Контрольная сумма автономного веб-клиента не совпала после записи.");
            }

            var actualIndex = File.ReadAllText(indexPath, Encoding.UTF8);
            if (!string.Equals(actualIndex, expectedIndex, StringComparison.Ordinal))
            {
                throw new InvalidDataException(
                    "index.html не совпал с ожидаемым содержимым после записи.");
            }

            if (CountOccurrences(actualIndex, BeginMarker) != 1
                || CountOccurrences(actualIndex, EndMarker) != 1)
            {
                throw new InvalidDataException(
                    "Управляемые маркеры автономного веб-клиента установлены некорректно.");
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
            string clientPath,
            bool previousClientExists,
            string? previousClient,
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
            if (previousClientExists && previousClient is not null)
            {
                WriteTextAtomically(
                    Path.Combine(transactionDirectory, ClientFileName),
                    previousClient);
                WriteTextAtomically(
                    Path.Combine(transactionDirectory, "client.existed"),
                    "true\n");
            }
            else
            {
                WriteTextAtomically(
                    Path.Combine(transactionDirectory, "client.existed"),
                    "false\n");
            }

            var manifest = JsonSerializer.Serialize(
                new
                {
                    createdUtc = DateTimeOffset.UtcNow,
                    indexPath,
                    clientPath,
                    previousClientExists,
                    targetDigest = digest
                },
                new JsonSerializerOptions
                {
                    WriteIndented = true
                });
            WriteTextAtomically(
                Path.Combine(transactionDirectory, ManifestFileName),
                manifest + Environment.NewLine);
            WriteRollbackScript(
                transactionDirectory,
                indexPath,
                clientPath,
                previousClientExists);
            WriteTextAtomically(
                Path.Combine(backupRoot, "LATEST"),
                transactionDirectory + Environment.NewLine);
            return transactionDirectory;
        }

        private static void WriteRollbackScript(
            string transactionDirectory,
            string indexPath,
            string clientPath,
            bool previousClientExists)
        {
            var backupIndexPath = Path.Combine(transactionDirectory, "index.html");
            var backupClientPath = Path.Combine(transactionDirectory, ClientFileName);
            var builder = new StringBuilder();
            builder.AppendLine("#!/bin/sh");
            builder.AppendLine("set -eu");
            builder.Append("cp -- ")
                .Append(EscapeShellArgument(backupIndexPath))
                .Append(' ')
                .AppendLine(EscapeShellArgument(indexPath));
            if (previousClientExists)
            {
                builder.Append("cp -- ")
                    .Append(EscapeShellArgument(backupClientPath))
                    .Append(' ')
                    .AppendLine(EscapeShellArgument(clientPath));
            }
            else
            {
                builder.Append("rm -f -- ")
                    .AppendLine(EscapeShellArgument(clientPath));
            }

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

        private void RestorePreviousInstallation(
            string indexPath,
            string originalIndex,
            string clientPath,
            bool previousClientExists,
            string? previousClient)
        {
            try
            {
                WriteTextAtomically(indexPath, originalIndex);
                if (previousClientExists && previousClient is not null)
                    WriteTextAtomically(clientPath, previousClient);
                else if (File.Exists(clientPath))
                    File.Delete(clientPath);

                _logger.LogWarning(
                    "Неуспешная установка автономного веб-клиента КиноПоиска автоматически отменена");
            }
            catch (Exception rollbackException)
            {
                _logger.LogCritical(
                    rollbackException,
                    "Автоматический откат автономного веб-клиента КиноПоиска завершился ошибкой");
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Critical,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.rollback-failed",
                    "Автоматический откат автономного веб-клиента КиноПоиска завершился ошибкой.",
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
