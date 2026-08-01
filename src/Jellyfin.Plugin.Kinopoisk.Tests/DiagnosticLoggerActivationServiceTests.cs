using System;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class DiagnosticLoggerActivationServiceTests
    {
        [Fact]
        public async Task ShouldAttachDiagnosticProviderToRuntimeLoggerFactoryOnce()
        {
            var loggerFactory = new RecordingLoggerFactory();
            var provider = new KinopoiskDiagnosticLoggerProvider();
            var service = new KinopoiskDiagnosticLoggerActivationService(
                loggerFactory,
                provider);

            await service.StartAsync(CancellationToken.None);
            await service.StartAsync(CancellationToken.None);

            Assert.Equal(1, loggerFactory.AddProviderCalls);
            Assert.Same(provider, loggerFactory.LastProvider);
        }

        private sealed class RecordingLoggerFactory : ILoggerFactory
        {
            public int AddProviderCalls { get; private set; }

            public ILoggerProvider LastProvider { get; private set; }

            public void AddProvider(ILoggerProvider provider)
            {
                AddProviderCalls++;
                LastProvider = provider;
            }

            public ILogger CreateLogger(string categoryName)
                => new NullLogger();

            public void Dispose()
            {
            }
        }

        private sealed class NullLogger : ILogger
        {
            public IDisposable BeginScope<TState>(TState state)
                where TState : notnull
                => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel)
                => false;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception exception,
                Func<TState, Exception, string> formatter)
            {
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static NullScope Instance { get; } = new();

            public void Dispose()
            {
            }
        }
    }
}
