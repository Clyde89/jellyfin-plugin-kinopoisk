#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Установлен автономный веб-клиент КиноПоиска без внешнего инжектора JavaScript.
    /// </summary>
    public sealed class KinopoiskStandaloneWebClientService : IHostedService
    {
        private const string BeginMarker = "<!-- KINOPOISK_WEB_CLIENT_BEGIN -->";
        private const string EndMarker = "<!-- KINOPOISK_WEB_CLIENT_END -->";
        private const string ClientFileName = "kinopoisk-web-client.js";

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

                var script = ReadEmbeddedScripts();
                var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(script)))
                    .ToLowerInvariant();
                var clientPath = Path.Combine(webPath, ClientFileName);
                WriteTextAtomically(clientPath, script);

                var originalIndex = File.ReadAllText(indexPath, Encoding.UTF8);
                var cleanIndex = RemoveManagedBlock(originalIndex);
                var scriptBlock = string.Join(
                    Environment.NewLine,
                    BeginMarker,
                    $"    <script src=\"{ClientFileName}?v={digest[..16]}\" defer></script>",
                    EndMarker);
                var bodyEnd = cleanIndex.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
                if (bodyEnd < 0)
                {
                    RecordFailure("BodyEndNotFound", "Закрывающий тег body в index.html не найден.");
                    return;
                }

                var updatedIndex = cleanIndex.Insert(bodyEnd, scriptBlock + Environment.NewLine);
                CreateInitialBackup(indexPath, originalIndex);
                WriteTextAtomically(indexPath, updatedIndex);

                KinopoiskWebTrailerIntegrationState.SetRegistered(true);
                _logger.LogInformation(
                    "Автономный веб-клиент КиноПоиска установлен в {WebPath}; SHA-256 {Digest}",
                    webPath,
                    digest);
                KinopoiskDiagnostics.Shared.RecordDiagnosticEvent(
                    LogLevel.Information,
                    GetType().FullName ?? nameof(KinopoiskStandaloneWebClientService),
                    "web-client.standalone.installed",
                    "Автономный веб-клиент КиноПоиска установлен.",
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["digest"] = digest,
                        ["resourceCount"] = ResourceNames.Length.ToString(System.Globalization.CultureInfo.InvariantCulture)
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

        private static string RemoveManagedBlock(string value)
        {
            var start = value.IndexOf(BeginMarker, StringComparison.Ordinal);
            if (start < 0)
                return value;

            var end = value.IndexOf(EndMarker, start, StringComparison.Ordinal);
            if (end < 0)
                throw new InvalidDataException("Начальный маркер веб-клиента найден без конечного маркера.");

            end += EndMarker.Length;
            while (end < value.Length && (value[end] == '\r' || value[end] == '\n'))
                end++;
            return value.Remove(start, end - start);
        }

        private void CreateInitialBackup(string indexPath, string originalIndex)
        {
            var plugin = Plugin.Instance
                ?? throw new InvalidOperationException("Экземпляр плагина КиноПоиск ещё не создан.");
            var backupDirectory = Path.Combine(plugin.DataFolderPath, "web-client-backup");
            Directory.CreateDirectory(backupDirectory);
            var backupPath = Path.Combine(backupDirectory, "index.html.before-kinopoisk");
            if (!File.Exists(backupPath))
                WriteTextAtomically(backupPath, originalIndex);
        }

        private static void WriteTextAtomically(string path, string content)
        {
            var directory = Path.GetDirectoryName(path)
                ?? throw new InvalidOperationException($"Родительский каталог не определён: {path}");
            Directory.CreateDirectory(directory);
            var temporaryPath = Path.Combine(
                directory,
                $".{Path.GetFileName(path)}.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
                File.Move(temporaryPath, path, true);
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
