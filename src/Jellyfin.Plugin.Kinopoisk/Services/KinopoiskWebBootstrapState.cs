#nullable enable

using System.Threading;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлено потокобезопасное состояние Runtime Web Bootstrap КиноПоиска.
    /// </summary>
    internal sealed class KinopoiskWebBootstrapState
    {
        private static KinopoiskWebBootstrapState? _current;

        private int _pipelineRegistered;
        private long _transformedResponses;
        private long _failures;
        private string? _lastEtag;

        public KinopoiskWebBootstrapState()
        {
            Volatile.Write(ref _current, this);
        }

        internal bool PipelineRegistered
            => Volatile.Read(ref _pipelineRegistered) == 1;

        internal long TransformedResponses
            => Interlocked.Read(ref _transformedResponses);

        internal long Failures
            => Interlocked.Read(ref _failures);

        internal string? LastEtag
            => Volatile.Read(ref _lastEtag);

        internal void MarkPipelineRegistered()
            => Interlocked.Exchange(ref _pipelineRegistered, 1);

        internal void RecordSuccess(string etag)
        {
            Interlocked.Increment(ref _transformedResponses);
            Interlocked.Exchange(ref _lastEtag, etag);
            KinopoiskWebTrailerIntegrationState.SetRegistered(true);
        }

        internal void RecordFailure()
            => Interlocked.Increment(ref _failures);

        internal static KinopoiskWebBootstrapSnapshot GetSnapshot(bool enabled)
        {
            var current = Volatile.Read(ref _current);
            return new KinopoiskWebBootstrapSnapshot
            {
                Enabled = enabled,
                PipelineRegistered = current?.PipelineRegistered == true,
                TransformedResponses = current?.TransformedResponses ?? 0,
                Failures = current?.Failures ?? 0,
                LastEtag = current?.LastEtag,
                BundleVersion = KinopoiskWebClientBundle.VersionToken,
                Mode = "runtime-http"
            };
        }
    }

    /// <summary>
    /// Добавлен диагностический снимок Runtime Web Bootstrap.
    /// </summary>
    public sealed class KinopoiskWebBootstrapSnapshot
    {
        public bool Enabled { get; init; }

        public bool PipelineRegistered { get; init; }

        public long TransformedResponses { get; init; }

        public long Failures { get; init; }

        public string? LastEtag { get; init; }

        public string BundleVersion { get; init; } = string.Empty;

        public string Mode { get; init; } = string.Empty;
    }
}