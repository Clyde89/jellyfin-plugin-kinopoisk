#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.ScheduledTasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public sealed class KinopoiskNativeTrailerCacheTests
    {
        [Fact]
        public void ShouldBuildCopyOnlyRemuxWithWhitelistedHeaders()
        {
            var arguments = new List<string>();

            KinopoiskNativeTrailerRemuxer.AddRemuxArguments(
                arguments,
                new Uri("https://strm.yandex.ru/trailer/master.m3u8"),
                new Dictionary<string, string>
                {
                    ["Referer"] = "https://widgets.kinopoisk.ru/trailer/1",
                    ["Origin"] = "https://widgets.kinopoisk.ru",
                    ["Authorization"] = "secret",
                    ["User-Agent"] = "Jellyfin test\r\nInjected: true"
                },
                "/cache/trailer.mp4");

            Assert.Contains("copy", arguments);
            Assert.DoesNotContain("libx264", arguments);
            Assert.DoesNotContain("hevc_qsv", arguments);
            Assert.DoesNotContain("secret", arguments);
            Assert.DoesNotContain(arguments, value => value.Contains("Injected", StringComparison.Ordinal));
            Assert.Contains("Origin: https://widgets.kinopoisk.ru\r\n", arguments);
            Assert.Equal("/cache/trailer.mp4", arguments[^1]);
        }

        [Fact]
        public async Task ShouldEvictLeastRecentlyUsedFilesAboveCapacity()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var options = new KinopoiskNativeTrailerCacheOptions
                {
                    CachePath = directory,
                    MaximumCacheBytes = 256L * 1024L * 1024L,
                    UnusedExpiration = TimeSpan.FromDays(30)
                };
                var cache = CreateCache(options);
                var older = Path.Combine(directory, "kinopoisk-1.mp4");
                var newer = Path.Combine(directory, "kinopoisk-2.mp4");
                CreateSparseFile(older, 160L * 1024L * 1024L);
                CreateSparseFile(newer, 160L * 1024L * 1024L);
                File.SetLastAccessTimeUtc(older, DateTime.UtcNow.AddDays(-2));
                File.SetLastAccessTimeUtc(newer, DateTime.UtcNow.AddDays(-1));

                var result = await cache.Cleanup(CancellationToken.None);

                Assert.Equal(1, result.RemovedCapacityFiles);
                Assert.False(File.Exists(older));
                Assert.True(File.Exists(newer));
                Assert.True(result.RemainingBytes <= options.MaximumCacheBytes);
                var snapshot = cache.GetSnapshot();
                Assert.Equal(1, snapshot.EvictedFileCount);
                Assert.Equal(160L * 1024L * 1024L, snapshot.EvictedBytes);
                Assert.Equal(1, snapshot.CleanupCount);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ShouldExposePlaybackHitAndMissStatistics()
        {
            var directory = CreateTemporaryDirectory();
            try
            {
                var cache = CreateCache(new KinopoiskNativeTrailerCacheOptions
                {
                    CachePath = directory
                });

                cache.RecordHit();
                cache.RecordHit();
                cache.RecordMiss();

                var snapshot = cache.GetSnapshot();
                Assert.Equal(2, snapshot.HitCount);
                Assert.Equal(1, snapshot.MissCount);
                Assert.Equal(66.67, snapshot.HitRatePercent);
                Assert.True(snapshot.StatisticsSinceUtc <= DateTimeOffset.UtcNow);
            }
            finally
            {
                Directory.Delete(directory, true);
            }
        }

        [Fact]
        public void ShouldScheduleWeeklyCleanupOnSunday()
        {
            var task = new KinopoiskNativeTrailerCacheCleanupTask(
                new DisabledCache(),
                NullLogger<KinopoiskNativeTrailerCacheCleanupTask>.Instance);

            var trigger = Assert.Single(task.GetDefaultTriggers());

            Assert.Equal(TaskTriggerInfoType.WeeklyTrigger, trigger.Type);
            Assert.Equal(DayOfWeek.Sunday, trigger.DayOfWeek);
            Assert.Equal(TimeSpan.FromHours(4).Add(TimeSpan.FromMinutes(15)).Ticks, trigger.TimeOfDayTicks);
        }

        private static KinopoiskNativeTrailerCache CreateCache(
            KinopoiskNativeTrailerCacheOptions options)
        {
            var mediaEncoder = DispatchProxy.Create<IMediaEncoder, EmptyDispatchProxy>();
            var remuxer = new KinopoiskNativeTrailerRemuxer(
                mediaEncoder,
                options,
                NullLogger<KinopoiskNativeTrailerRemuxer>.Instance);
            return new KinopoiskNativeTrailerCache(
                options,
                remuxer,
                NullLogger<KinopoiskNativeTrailerCache>.Instance);
        }

        private static string CreateTemporaryDirectory()
        {
            var path = Path.Combine(
                Path.GetTempPath(),
                "kinopoisk-trailer-cache-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(path);
            return path;
        }

        private static void CreateSparseFile(string path, long length)
        {
            using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.SetLength(length);
        }

        public class EmptyDispatchProxy : DispatchProxy
        {
            protected override object? Invoke(MethodInfo? targetMethod, object?[]? args)
                => targetMethod?.ReturnType.IsValueType == true
                    ? Activator.CreateInstance(targetMethod.ReturnType)
                    : null;
        }

        private sealed class DisabledCache : IKinopoiskNativeTrailerCache
        {
            public bool Enabled => false;

            public string GetExpectedPath(int kinopoiskId) => string.Empty;

            public KinopoiskNativeTrailerCacheEntry? TryGet(int kinopoiskId, bool touch = true) => null;

            public void RecordHit()
            {
            }

            public void RecordMiss()
            {
            }

            public Task<KinopoiskNativeTrailerCacheEntry?> GetOrCreate(
                int kinopoiskId,
                Jellyfin.Plugin.Kinopoisk.Playback.KinopoiskTrailerPlaybackSource source,
                CancellationToken cancellationToken)
                => Task.FromResult<KinopoiskNativeTrailerCacheEntry?>(null);

            public Task<KinopoiskNativeTrailerCacheCleanupResult> Cleanup(
                CancellationToken cancellationToken)
                => Task.FromResult(new KinopoiskNativeTrailerCacheCleanupResult());

            public KinopoiskNativeTrailerCacheSnapshot GetSnapshot()
                => new() { Enabled = false };
        }
    }
}
