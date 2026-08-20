#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Playback;
using MediaBrowser.Controller.MediaEncoding;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Без перекодирования преобразует HLS H.264/AAC в локальный MP4.
    /// </summary>
    public sealed class KinopoiskNativeTrailerRemuxer
    {
        private const long MinimumValidFileBytes = 64L * 1024L;

        private readonly IMediaEncoder _mediaEncoder;
        private readonly KinopoiskNativeTrailerCacheOptions _options;
        private readonly ILogger<KinopoiskNativeTrailerRemuxer> _logger;

        public KinopoiskNativeTrailerRemuxer(
            IMediaEncoder mediaEncoder,
            KinopoiskNativeTrailerCacheOptions options,
            ILogger<KinopoiskNativeTrailerRemuxer> logger)
        {
            _mediaEncoder = mediaEncoder ?? throw new ArgumentNullException(nameof(mediaEncoder));
            _options = options ?? throw new ArgumentNullException(nameof(options));
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        public async Task<KinopoiskNativeTrailerCacheEntry> Remux(
            int kinopoiskId,
            KinopoiskTrailerPlaybackSource source,
            string outputPath,
            CancellationToken cancellationToken)
        {
            var mediaUri = ValidateSource(source);
            var startInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            AddRemuxArguments(startInfo.ArgumentList, mediaUri, source.RequestHeaders, outputPath);

            var startedUtc = DateTimeOffset.UtcNow;
            var exitCode = await RunProcess(
                    startInfo,
                    _options.RemuxTimeout,
                    cancellationToken)
                .ConfigureAwait(false);
            if (exitCode != 0)
                throw new IOException($"FFmpeg завершил remux трейлера с кодом {exitCode.ToString(CultureInfo.InvariantCulture)}.");

            var file = new FileInfo(outputPath);
            if (!file.Exists || file.Length < MinimumValidFileBytes)
                throw new IOException("FFmpeg не создал пригодный локальный MP4 трейлера.");
            if (file.Length > _options.MaximumFileBytes)
                throw new IOException("Локальный трейлер превысил безопасный предел одного файла.");

            var entry = await Probe(
                    kinopoiskId,
                    outputPath,
                    file.Length,
                    startedUtc,
                    cancellationToken)
                .ConfigureAwait(false);

            _logger.LogInformation(
                "Трейлер Kinopoisk ID {KinopoiskId} сохранён в локальный MP4 без перекодирования: {TrailerBytes} байт, {Width}x{Height}, видео {VideoCodec}, аудио {AudioCodec}",
                kinopoiskId,
                file.Length,
                entry.Width,
                entry.Height,
                entry.VideoCodec,
                entry.AudioCodec);
            return entry;
        }

        internal static void AddRemuxArguments(
            ICollection<string> arguments,
            Uri mediaUri,
            IReadOnlyDictionary<string, string> requestHeaders,
            string outputPath)
        {
            arguments.Add("-hide_banner");
            arguments.Add("-loglevel");
            arguments.Add("error");
            arguments.Add("-nostdin");
            arguments.Add("-y");
            arguments.Add("-rw_timeout");
            arguments.Add("15000000");
            arguments.Add("-analyzeduration");
            arguments.Add("5000000");
            arguments.Add("-probesize");
            arguments.Add("10000000");

            var safeHeaders = NormalizeHeaders(requestHeaders);
            if (safeHeaders.TryGetValue("User-Agent", out var userAgent))
            {
                arguments.Add("-user_agent");
                arguments.Add(userAgent);
            }

            if (safeHeaders.TryGetValue("Referer", out var referer))
            {
                arguments.Add("-referer");
                arguments.Add(referer);
            }

            var additionalHeaders = safeHeaders
                .Where(pair => !string.Equals(pair.Key, "User-Agent", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(pair.Key, "Referer", StringComparison.OrdinalIgnoreCase))
                .Select(pair => $"{pair.Key}: {pair.Value}\r\n")
                .ToArray();
            if (additionalHeaders.Length > 0)
            {
                arguments.Add("-headers");
                arguments.Add(string.Concat(additionalHeaders));
            }

            arguments.Add("-i");
            arguments.Add(mediaUri.AbsoluteUri);
            arguments.Add("-map");
            arguments.Add("0:v:0");
            arguments.Add("-map");
            arguments.Add("0:a:0");
            arguments.Add("-c:v");
            arguments.Add("copy");
            arguments.Add("-c:a");
            arguments.Add("copy");
            arguments.Add("-bsf:a");
            arguments.Add("aac_adtstoasc");
            arguments.Add("-movflags");
            arguments.Add("+faststart");
            arguments.Add("-avoid_negative_ts");
            arguments.Add("make_zero");
            arguments.Add("-max_muxing_queue_size");
            arguments.Add("2048");
            arguments.Add("-t");
            arguments.Add("600");
            arguments.Add(outputPath);
        }

        private async Task<KinopoiskNativeTrailerCacheEntry> Probe(
            int kinopoiskId,
            string path,
            long contentLength,
            DateTimeOffset createdUtc,
            CancellationToken cancellationToken)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _mediaEncoder.ProbePath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };
            foreach (var value in new[]
            {
                "-v", "error",
                "-show_streams",
                "-show_format",
                "-of", "json",
                path
            })
            {
                startInfo.ArgumentList.Add(value);
            }

            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new IOException("Не удалось запустить ffprobe для локального трейлера.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForExit(process, TimeSpan.FromSeconds(30), cancellationToken)
                .ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            if (process.ExitCode != 0)
                throw new IOException("ffprobe не подтвердил локальный трейлер.");

            var json = await stdout.ConfigureAwait(false);
            using var document = JsonDocument.Parse(json);
            var streams = document.RootElement.GetProperty("streams").EnumerateArray().ToArray();
            var video = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "video");
            var audio = streams.FirstOrDefault(stream => GetString(stream, "codec_type") == "audio");
            if (video.ValueKind == JsonValueKind.Undefined
                || audio.ValueKind == JsonValueKind.Undefined)
            {
                throw new IOException("Локальный трейлер должен содержать видео и аудио.");
            }

            var videoCodec = GetString(video, "codec_name");
            var audioCodec = GetString(audio, "codec_name");
            if (string.IsNullOrWhiteSpace(videoCodec) || string.IsNullOrWhiteSpace(audioCodec))
                throw new IOException("ffprobe не определил кодеки локального трейлера.");

            var format = document.RootElement.TryGetProperty("format", out var formatElement)
                ? formatElement
                : default;
            var durationSeconds = GetDouble(format, "duration");
            var now = DateTimeOffset.UtcNow;
            return new KinopoiskNativeTrailerCacheEntry
            {
                KinopoiskId = kinopoiskId,
                Path = path,
                ContentLength = contentLength,
                CreatedUtc = createdUtc,
                LastAccessUtc = now,
                Container = "mp4",
                VideoCodec = videoCodec,
                VideoProfile = GetString(video, "profile"),
                VideoLevel = GetDouble(video, "level"),
                VideoBitRate = GetInt(video, "bit_rate"),
                Width = GetInt(video, "width"),
                Height = GetInt(video, "height"),
                FrameRate = ParseFrameRate(GetString(video, "avg_frame_rate")),
                VideoBitDepth = GetInt(video, "bits_per_raw_sample"),
                PixelFormat = GetString(video, "pix_fmt"),
                AudioCodec = audioCodec,
                AudioBitRate = GetInt(audio, "bit_rate"),
                AudioChannels = GetInt(audio, "channels"),
                AudioSampleRate = GetInt(audio, "sample_rate"),
                RunTimeTicks = durationSeconds.HasValue
                    ? TimeSpan.FromSeconds(durationSeconds.Value).Ticks
                    : null,
                TotalBitRate = GetInt(format, "bit_rate")
            };
        }

        private static Uri ValidateSource(KinopoiskTrailerPlaybackSource source)
        {
            ArgumentNullException.ThrowIfNull(source);
            if (!string.Equals(source.Provider, "kinopoisk", StringComparison.OrdinalIgnoreCase)
                || !string.Equals(source.PlaybackKind, "hls", StringComparison.OrdinalIgnoreCase)
                || !Uri.TryCreate(source.Url, UriKind.Absolute, out var mediaUri)
                || mediaUri.Scheme != Uri.UriSchemeHttps
                || !string.Equals(mediaUri.Host, "strm.yandex.ru", StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("Для локального кэша разрешён только HLS с strm.yandex.ru.", nameof(source));
            }

            return mediaUri;
        }

        private static Dictionary<string, string> NormalizeHeaders(
            IReadOnlyDictionary<string, string> requestHeaders)
        {
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (requestHeaders is null)
                return result;

            var allowed = new HashSet<string>(
                new[] { "Referer", "Origin", "User-Agent" },
                StringComparer.OrdinalIgnoreCase);
            foreach (var pair in requestHeaders)
            {
                if (!allowed.Contains(pair.Key)
                    || string.IsNullOrWhiteSpace(pair.Value)
                    || pair.Value.Contains('\r')
                    || pair.Value.Contains('\n'))
                {
                    continue;
                }

                result[pair.Key] = pair.Value.Trim();
            }

            return result;
        }

        private static async Task<int> RunProcess(
            ProcessStartInfo startInfo,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                throw new IOException("Не удалось запустить FFmpeg.");

            var stdout = process.StandardOutput.ReadToEndAsync();
            var stderr = process.StandardError.ReadToEndAsync();
            await WaitForExit(process, timeout, cancellationToken).ConfigureAwait(false);
            _ = await stdout.ConfigureAwait(false);
            _ = await stderr.ConfigureAwait(false);
            return process.ExitCode;
        }

        private static async Task WaitForExit(
            Process process,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(timeout > TimeSpan.Zero ? timeout : TimeSpan.FromMinutes(10));
            try
            {
                await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                if (cancellationToken.IsCancellationRequested)
                    throw;

                throw new TimeoutException("Превышено время подготовки локального трейлера.");
            }
        }

        private static void TryKill(Process process)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(true);
            }
            catch
            {
                // Процесс уже завершился либо недоступен.
            }
        }

        private static string GetString(JsonElement element, string propertyName)
            => element.ValueKind == JsonValueKind.Object
                && element.TryGetProperty(propertyName, out var property)
                ? property.ValueKind == JsonValueKind.String
                    ? property.GetString() ?? string.Empty
                    : property.GetRawText().Trim('"')
                : string.Empty;

        private static int? GetInt(JsonElement element, string propertyName)
        {
            var value = GetString(element, propertyName);
            return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        private static double? GetDouble(JsonElement element, string propertyName)
        {
            var value = GetString(element, propertyName);
            return double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }

        private static float? ParseFrameRate(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return null;
            var parts = value.Split('/', StringSplitOptions.TrimEntries);
            if (parts.Length == 2
                && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var numerator)
                && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var denominator)
                && denominator > 0)
            {
                return (float)(numerator / denominator);
            }

            return float.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var result)
                ? result
                : null;
        }
    }
}
