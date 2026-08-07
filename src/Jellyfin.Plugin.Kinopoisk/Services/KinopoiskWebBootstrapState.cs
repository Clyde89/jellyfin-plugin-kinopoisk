#nullable enable

using System.Threading;

namespace Jellyfin.Plugin.Kinopoisk.Services
{
    /// <summary>
    /// Добавлено потокобезопасное состояние Runtime Web Bootstrap КиноПоиска.
    /// </summary>
    internal sealed class KinopoiskWebBootstrapState
    {
        private int _pipelineRegistered;
        private long _transformedResponses;
        private long _failures;
        private string? _lastEtag;

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
    }
}
