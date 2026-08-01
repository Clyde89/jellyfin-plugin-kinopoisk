using System;
using System.Collections.Generic;
using KinopoiskUnofficialInfo.ApiClient;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class DiagnosticDirectEventSinkTests
    {
        [Fact]
        public void ShouldForwardSessionAndRuntimeCountersToDirectSink()
        {
            var diagnostics = new KinopoiskDiagnostics();
            var sink = new RecordingSink();
            diagnostics.AttachDiagnosticSink(sink);

            diagnostics.StartDiagnosticSession();
            diagnostics.RecordApiRequest();
            diagnostics.RecordApiFailure();
            diagnostics.RecordPersistentCacheWrite();

            var snapshot = diagnostics.GetSnapshot();

            Assert.True(sink.SessionStarted);
            Assert.Collection(
                sink.Events,
                item => Assert.Equal("diagnostic.session.started", item.EventName),
                item => Assert.Equal("api.request", item.EventName),
                item => Assert.Equal("api.failure", item.EventName),
                item => Assert.Equal("cache.persistent.write", item.EventName));
            Assert.True(snapshot.DiagnosticSinkAttached);
            Assert.Equal(4, snapshot.DiagnosticEventsReceived);
            Assert.Equal(4, snapshot.DiagnosticEventsWritten);
            Assert.Equal(1, snapshot.ApiRequests);
            Assert.Equal(1, snapshot.ApiFailures);
            Assert.Equal(1, snapshot.PersistentCacheWrites);
        }

        [Fact]
        public void ShouldIsolateDirectSinkFailureFromProviderRuntime()
        {
            var diagnostics = new KinopoiskDiagnostics();
            diagnostics.AttachDiagnosticSink(new ThrowingSink());

            var exception = Record.Exception(() =>
            {
                diagnostics.StartDiagnosticSession();
                diagnostics.RecordApiRequest();
                diagnostics.RecordApiFailure();
            });

            Assert.Null(exception);
            Assert.Equal(1, diagnostics.GetSnapshot().ApiRequests);
            Assert.Equal(1, diagnostics.GetSnapshot().ApiFailures);
        }

        private sealed class RecordingSink : IKinopoiskDiagnosticSink
        {
            public bool SessionStarted { get; private set; }

            public List<KinopoiskDiagnosticEvent> Events { get; } = new();

            public void BeginSession()
            {
                SessionStarted = true;
                Events.Clear();
            }

            public void Write(KinopoiskDiagnosticEvent diagnosticEvent)
                => Events.Add(diagnosticEvent);

            public KinopoiskDiagnosticSinkSnapshot GetSnapshot()
            {
                return new KinopoiskDiagnosticSinkSnapshot
                {
                    Attached = true,
                    EventsReceived = Events.Count,
                    EventsWritten = Events.Count,
                    LastEventName = Events.Count == 0
                        ? string.Empty
                        : Events[^1].EventName
                };
            }
        }

        private sealed class ThrowingSink : IKinopoiskDiagnosticSink
        {
            public void BeginSession()
                => throw new InvalidOperationException("test");

            public void Write(KinopoiskDiagnosticEvent diagnosticEvent)
                => throw new InvalidOperationException("test");

            public KinopoiskDiagnosticSinkSnapshot GetSnapshot()
                => throw new InvalidOperationException("test");
        }
    }
}
