using System;
using System.Linq;
using System.Reflection;
using Jellyfin.Plugin.Kinopoisk.MetadataProviders;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class RuntimeActivationTests
    {
        [Fact]
        public void ShouldRegisterServicesBeforePluginInstanceIsCreated()
        {
            var instanceField = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin)
                .GetField("<Instance>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
            Assert.NotNull(instanceField);

            var previousInstance = instanceField.GetValue(null);
            instanceField.SetValue(null, null);

            try
            {
                var services = new ServiceCollection();
                var registrator = new KinopoiskPluginServiceRegistrator();

                var exception = Record.Exception(() =>
                    registrator.RegisterServices(services, null!));

                Assert.Null(exception);
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(KinopoiskFranchiseLibraryScanner));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(IStartupFilter)
                        && descriptor.ImplementationType == typeof(KinopoiskWebBootstrapStartupFilter));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(IKinopoiskTrailerStreamResolver)
                        && descriptor.ImplementationType == typeof(KinopoiskTrailerStreamResolver));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(KinopoiskTrailerPlaybackService));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(IKinopoiskTrailerPlaybackService));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(KinopoiskNativeTrailerBridge));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType
                        == typeof(IKinopoiskNativeTrailerCacheWarmupService));
                Assert.Contains(
                    services,
                    descriptor => descriptor.ServiceType == typeof(IMediaSourceProvider)
                        && descriptor.ImplementationType
                            == typeof(KinopoiskNativeTrailerMediaSourceProvider));
                Assert.DoesNotContain(
                    services,
                    descriptor => descriptor.ServiceType == typeof(IHostedService)
                        && descriptor.ImplementationType == typeof(KinopoiskStandaloneWebClientService));
            }
            finally
            {
                instanceField.SetValue(null, previousInstance);
            }
        }

        [Theory]
        [InlineData(typeof(PersonImageProvider))]
        [InlineData(typeof(VideoImageProvider))]
        [InlineData(typeof(MovieMetadataProvider))]
        [InlineData(typeof(SeriesMetadataProvider))]
        public void ShouldDeclareSinglePreferredRuntimeConstructor(Type providerType)
        {
            var publicConstructors = providerType.GetConstructors();
            Assert.True(publicConstructors.Length > 1);

            var preferredConstructors = publicConstructors
                .Where(constructor => constructor
                    .GetCustomAttributes(typeof(ActivatorUtilitiesConstructorAttribute), inherit: false)
                    .Any())
                .ToArray();

            var preferredConstructor = Assert.Single(preferredConstructors);
            Assert.Equal(
                publicConstructors.Max(constructor => constructor.GetParameters().Length),
                preferredConstructor.GetParameters().Length);
        }
    }
}
