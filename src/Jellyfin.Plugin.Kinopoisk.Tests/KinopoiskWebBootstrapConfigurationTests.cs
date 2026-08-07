#nullable enable

using Jellyfin.Plugin.Kinopoisk.Configuration;
using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskWebBootstrapConfigurationTests
    {
        [Fact]
        public void ShouldEnableRuntimeWebBootstrapByDefault()
        {
            var configuration = new PluginConfiguration();

            Assert.True(configuration.EnableWebBootstrap);
            Assert.True(configuration.WebBootstrap.Enabled);
        }

        [Fact]
        public void ShouldExposeRuntimeBootstrapSnapshot()
        {
            var state = new KinopoiskWebBootstrapState();
            state.MarkPipelineRegistered();
            state.RecordSuccess("\"kp-test\"");
            var configuration = new PluginConfiguration
            {
                EnableWebBootstrap = false
            };

            var snapshot = configuration.WebBootstrap;

            Assert.False(snapshot.Enabled);
            Assert.True(snapshot.PipelineRegistered);
            Assert.Equal(1, snapshot.TransformedResponses);
            Assert.Equal(0, snapshot.Failures);
            Assert.Equal("\"kp-test\"", snapshot.LastEtag);
            Assert.Equal("runtime-http", snapshot.Mode);
            Assert.NotEmpty(snapshot.BundleVersion);
        }
    }
}
