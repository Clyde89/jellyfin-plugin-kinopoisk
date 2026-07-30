using Jellyfin.Plugin.Kinopoisk.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PluginConfigurationTests
    {
        [Fact]
        public void ShouldNotContainDefaultApiToken()
        {
            var configuration = new PluginConfiguration();

            Assert.True(string.IsNullOrWhiteSpace(configuration.ApiToken));
        }
    }
}
