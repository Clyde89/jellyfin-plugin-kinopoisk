using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using KinopoiskUnofficialInfo.ApiClient;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    public sealed class KinopoiskDiagnosticFileSink : IKinopoiskDiagnosticSink
    {
        private static readonly object FileSync = new();
        private static readonly Regex AuthorizationHeaderRegex = new(
            @"(?i)\bauthorization\b\s*[:=]\s*[^\r\n,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex SecretRegex = new(
            @"(?i)\b(x-api-key|api[_-]?token|token)\b\s*[:=]\s*[^\s,;]+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);
        private static readonly Regex UrlRegex = new(
            "https?://[^\\s\\]\\)\\}\\\"']+",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
        private static readonly JsonSerializerOptions JsonOptions = new()
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            WriteIndented = false
        };

        private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;
        private long _eventsReceived;
        private long _eventsWritten;
        private long _writeFailures;
        private DateTimeOffset? _lastWriteUtc;
        private string _currentFilePath = string.Empty;
        private string _lastEventName = string.Empty;
        private string _lastError = string.Empty;

        public static KinopoiskDiagnosticFileSink Shared { get; } = new();

        public void BeginSession()
        {
            lock (FileSync)
            {
                _eventsReceived = 0;
                _eventsWritten = 0;
                _writeFailures = 0;
                _lastWriteUtc = null;
                _lastEventName = string.Empty;
                _lastError = string.Empty;
                _currentFilePath = BuildCurrentFilePath();
            }
        }

        public void Write(KinopoiskDiagnosticEvent diagnosticEvent)
        {
            ArgumentNullException.ThrowIfNull(diagnosticEvent);

            lock (FileSync)
            {
                _eventsReceived++;
                _lastEventName = diagnosticEvent.EventName ?? string.Empty;

                var configuration = Plugin.Instance?.Configuration;
                if (!ShouldWrite(configuration, diagnosticEvent.Level))
                    return;

                try
                {
                    var directory = Path.Combine(Plugin.Instance.DataFolderPath, "diagnostics");
                    Directory.CreateDirectory(directory);

                    var sessionId = SanitizeFileName(configuration.DiagnosticSessionId);
                    var path = Path.Combine(
                        directory,
                        $"kinopoisk-diagnostic-{sessionId}.jsonl");
                    _currentFilePath = path;

                    var maximumBytes = Math.Max(1, configuration.DiagnosticMaximumFileMegabytes)
                        * 1024L
                        * 1024L;

                    if (File.Exists(path) && new FileInfo(path).Length >= maximumBytes)
                    {
                        var archivePath = Path.Combine(
                            directory,
                            $"kinopoisk-diagnostic-{sessionId}-part-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.jsonl");
                        File.Move(path, archivePath, true);
                    }

                    if (!File.Exists(path))
                        WriteHeader(path, configuration);

                    var record = new Dictionary<string, object>
                    {
                        ["timestampUtc"] = diagnosticEvent.TimestampUtc == default
                            ? DateTimeOffset.UtcNow
                            : diagnosticEvent.TimestampUtc,
                        ["sessionId"] = configuration.DiagnosticSessionId,
                        ["level"] = diagnosticEvent.Level.ToString(),
                        ["category"] = Sanitize(diagnosticEvent.Category),
                        ["eventName"] = Sanitize(diagnosticEvent.EventName),
                        ["message"] = Sanitize(diagnosticEvent.Message)
                    };

                    if (diagnosticEvent.Fields is not null && diagnosticEvent.Fields.Count > 0)
                    {
                        record["fields"] = diagnosticEvent.Fields.ToDictionary(
                            item => Sanitize(item.Key),
                            item => Sanitize(item.Value),
                            StringComparer.OrdinalIgnoreCase);
                    }

                    File.AppendAllText(
                        path,
                        JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);

                    _eventsWritten++;
                    _lastWriteUtc = DateTimeOffset.UtcNow;
                    _lastError = string.Empty;

                    if (DateTimeOffset.UtcNow - _lastCleanupUtc > TimeSpan.FromMinutes(5))
                    {
                        Cleanup(directory, configuration.DiagnosticRetentionFiles);
                        _lastCleanupUtc = DateTimeOffset.UtcNow;
                    }
                }
                catch (Exception exception)
                {
                    _writeFailures++;
                    _lastError = $"{exception.GetType().Name}: {Sanitize(exception.Message)}";
                }
            }
        }

        public KinopoiskDiagnosticSinkSnapshot GetSnapshot()
        {
            lock (FileSync)
            {
                return new KinopoiskDiagnosticSinkSnapshot
                {
                    Attached = true,
                    EventsReceived = _eventsReceived,
                    EventsWritten = _eventsWritten,
                    WriteFailures = _writeFailures,
                    LastWriteUtc = _lastWriteUtc,
                    CurrentFilePath = _currentFilePath,
                    LastEventName = _lastEventName,
                    LastError = _lastError
                };
            }
        }

        private static bool ShouldWrite(
            PluginConfiguration configuration,
            LogLevel level)
        {
            if (configuration is null
                || !configuration.EnableDiagnosticMode
                || string.IsNullOrWhiteSpace(configuration.DiagnosticSessionId)
                || !configuration.DiagnosticSessionExpiresUtc.HasValue
                || configuration.DiagnosticSessionExpiresUtc.Value <= DateTimeOffset.UtcNow)
            {
                return false;
            }

            var minimumLevel = configuration.DiagnosticLogLevel switch
            {
                DiagnosticLogLevel.Basic => LogLevel.Information,
                DiagnosticLogLevel.Detailed => LogLevel.Debug,
                DiagnosticLogLevel.Trace => LogLevel.Trace,
                _ => LogLevel.Debug
            };

            return level >= minimumLevel && level != LogLevel.None;
        }

        private static void WriteHeader(
            string path,
            PluginConfiguration configuration)
        {
            var header = new Dictionary<string, object>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = configuration.DiagnosticSessionId,
                ["level"] = "Information",
                ["category"] = typeof(KinopoiskDiagnosticFileSink).FullName,
                ["eventName"] = "diagnostic.file.created",
                ["message"] = "Файл диагностической сессии КиноПоиска создан.",
                ["startedUtc"] = configuration.DiagnosticSessionStartedUtc,
                ["expiresUtc"] = configuration.DiagnosticSessionExpiresUtc,
                ["diagnosticLevel"] = configuration.DiagnosticLogLevel.ToString(),
                ["writer"] = "direct-event-sink"
            };

            File.AppendAllText(
                path,
                JsonSerializer.Serialize(header, JsonOptions) + Environment.NewLine);
        }

        private static void Cleanup(string directory, int retentionFiles)
        {
            var files = new DirectoryInfo(directory)
                .EnumerateFiles("kinopoisk-diagnostic-*.jsonl", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(Math.Max(1, retentionFiles))
                .ToArray();

            foreach (var file in files)
            {
                try
                {
                    file.Delete();
                }
                catch
                {
                }
            }
        }

        private static string BuildCurrentFilePath()
        {
            var plugin = Plugin.Instance;
            var sessionId = plugin?.Configuration?.DiagnosticSessionId;
            if (plugin is null || string.IsNullOrWhiteSpace(sessionId))
                return string.Empty;

            return Path.Combine(
                plugin.DataFolderPath,
                "diagnostics",
                $"kinopoisk-diagnostic-{SanitizeFileName(sessionId)}.jsonl");
        }

        private static string Sanitize(string value)
        {
            if (string.IsNullOrEmpty(value))
                return string.Empty;

            var sanitized = AuthorizationHeaderRegex.Replace(value, "authorization=***");
            sanitized = SecretRegex.Replace(sanitized, "$1=***");
            sanitized = UrlRegex.Replace(sanitized, match =>
            {
                if (!Uri.TryCreate(match.Value, UriKind.Absolute, out var uri))
                    return "[URL]";

                return uri.GetLeftPart(UriPartial.Path);
            });

            return sanitized.Length <= 4000
                ? sanitized
                : sanitized[..4000] + "…";
        }

        private static string SanitizeFileName(string value)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var result = new string((value ?? string.Empty)
                .Where(character => !invalid.Contains(character))
                .ToArray());
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
        }
    }
}
