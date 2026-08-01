using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Jellyfin.Plugin.Kinopoisk.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    [ProviderAlias("KinopoiskDiagnostic")]
    public sealed class KinopoiskDiagnosticLoggerProvider : ILoggerProvider, ISupportExternalScope
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

        private IExternalScopeProvider _scopeProvider = new LoggerExternalScopeProvider();
        private DateTimeOffset _lastCleanupUtc = DateTimeOffset.MinValue;

        public ILogger CreateLogger(string categoryName)
            => new DiagnosticLogger(this, categoryName);

        public void Dispose()
        {
        }

        public void SetScopeProvider(IExternalScopeProvider scopeProvider)
            => _scopeProvider = scopeProvider ?? new LoggerExternalScopeProvider();

        private bool IsEnabled(string categoryName, LogLevel logLevel)
        {
            if (logLevel == LogLevel.None || !IsSupportedCategory(categoryName))
                return false;

            var configuration = Plugin.Instance?.Configuration;
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

            return logLevel >= minimumLevel;
        }

        private void Write<TState>(
            string categoryName,
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception exception,
            Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(categoryName, logLevel))
                return;

            var configuration = Plugin.Instance?.Configuration;
            if (configuration is null)
                return;

            string message;
            try
            {
                message = formatter(state, exception);
            }
            catch
            {
                message = Convert.ToString(state, CultureInfo.InvariantCulture) ?? string.Empty;
            }

            var scopes = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            _scopeProvider.ForEachScope((scope, values) => AddScope(values, scope), scopes);

            var record = new Dictionary<string, object>
            {
                ["timestampUtc"] = DateTimeOffset.UtcNow,
                ["sessionId"] = configuration.DiagnosticSessionId,
                ["level"] = logLevel.ToString(),
                ["category"] = categoryName,
                ["eventId"] = eventId.Id,
                ["eventName"] = eventId.Name,
                ["threadId"] = Environment.CurrentManagedThreadId,
                ["message"] = Sanitize(message)
            };

            if (scopes.Count > 0)
                record["scope"] = scopes;

            if (exception is not null)
            {
                record["exceptionType"] = exception.GetType().FullName;
                record["exceptionMessage"] = Sanitize(exception.Message);
                if (exception.InnerException is not null)
                {
                    record["innerExceptionType"] = exception.InnerException.GetType().FullName;
                    record["innerExceptionMessage"] = Sanitize(exception.InnerException.Message);
                }
            }

            TryAppend(configuration, record);
        }

        private void TryAppend(
            PluginConfiguration configuration,
            IReadOnlyDictionary<string, object> record)
        {
            try
            {
                lock (FileSync)
                {
                    var directory = Path.Combine(Plugin.Instance.DataFolderPath, "diagnostics");
                    Directory.CreateDirectory(directory);

                    var sessionId = SanitizeFileName(configuration.DiagnosticSessionId);
                    var path = Path.Combine(
                        directory,
                        $"kinopoisk-diagnostic-{sessionId}.jsonl");
                    var maximumBytes = configuration.DiagnosticMaximumFileMegabytes
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
                    {
                        var header = new Dictionary<string, object>
                        {
                            ["timestampUtc"] = DateTimeOffset.UtcNow,
                            ["sessionId"] = configuration.DiagnosticSessionId,
                            ["level"] = "Information",
                            ["category"] = typeof(KinopoiskDiagnosticLoggerProvider).FullName,
                            ["eventName"] = "diagnostic.session.started",
                            ["message"] = "Диагностическая сессия КиноПоиска начата.",
                            ["startedUtc"] = configuration.DiagnosticSessionStartedUtc,
                            ["expiresUtc"] = configuration.DiagnosticSessionExpiresUtc,
                            ["diagnosticLevel"] = configuration.DiagnosticLogLevel.ToString()
                        };
                        File.AppendAllText(
                            path,
                            JsonSerializer.Serialize(header, JsonOptions) + Environment.NewLine);
                    }

                    File.AppendAllText(
                        path,
                        JsonSerializer.Serialize(record, JsonOptions) + Environment.NewLine);

                    if (DateTimeOffset.UtcNow - _lastCleanupUtc > TimeSpan.FromMinutes(5))
                    {
                        Cleanup(directory, configuration.DiagnosticRetentionFiles);
                        _lastCleanupUtc = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch
            {
            }
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

        private static void AddScope(IDictionary<string, object> target, object scope)
        {
            if (scope is IEnumerable<KeyValuePair<string, object>> values)
            {
                foreach (var pair in values)
                {
                    if (string.IsNullOrWhiteSpace(pair.Key))
                        continue;

                    target[pair.Key] = Sanitize(
                        Convert.ToString(pair.Value, CultureInfo.InvariantCulture) ?? string.Empty);
                }

                return;
            }

            var text = Sanitize(Convert.ToString(scope, CultureInfo.InvariantCulture) ?? string.Empty);
            if (!string.IsNullOrWhiteSpace(text))
                target[$"scope{target.Count + 1}"] = text;
        }

        private static bool IsSupportedCategory(string categoryName)
            => categoryName.StartsWith("Jellyfin.Plugin.Kinopoisk", StringComparison.Ordinal)
                || categoryName.StartsWith("KinopoiskUnofficialInfo.ApiClient", StringComparison.Ordinal);

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
            var result = new string(value
                .Where(character => !invalid.Contains(character))
                .ToArray());
            return string.IsNullOrWhiteSpace(result) ? "unknown" : result;
        }

        private sealed class DiagnosticLogger : ILogger
        {
            private readonly KinopoiskDiagnosticLoggerProvider _provider;
            private readonly string _categoryName;

            public DiagnosticLogger(
                KinopoiskDiagnosticLoggerProvider provider,
                string categoryName)
            {
                _provider = provider;
                _categoryName = categoryName;
            }

            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
                => _provider._scopeProvider.Push(state);

            public bool IsEnabled(LogLevel logLevel)
                => _provider.IsEnabled(_categoryName, logLevel);

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
                if (formatter is null)
                    throw new ArgumentNullException(nameof(formatter));

                _provider.Write(
                    _categoryName,
                    logLevel,
                    eventId,
                    state,
                    exception,
                    formatter);
            }
        }
    }
}
