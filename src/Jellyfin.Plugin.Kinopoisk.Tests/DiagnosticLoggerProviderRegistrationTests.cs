using System.Linq;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class DiagnosticLoggerProviderRegistrationTests
    {
        [Fact]
        public void ShouldRegisterDedicatedDiagnosticLoggerActivationService()
        {
            var services = new ServiceCollection();
            var registrator = new KinopoiskPluginServiceRegistrator();

            registrator.RegisterServices(services, null!);

            Assert.Contains(
                services,
                descriptor => descriptor.ServiceType == typeof(KinopoiskDiagnosticLoggerProvider));
            Assert.Contains(
                services,
                descriptor => descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(KinopoiskDiagnosticLoggerActivationService));
            Assert.DoesNotContain(
                services,
                descriptor => descriptor.ServiceType == typeof(ILoggerProvider));
            Assert.Contains(
                services,
                descriptor => descriptor.ServiceType == typeof(Microsoft.Extensions.Options.IConfigureOptions<LoggerFilterOptions>));
        }
    }
}
