using Jellyfin.Plugin.Kinopoisk.Configuration;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class TrailerConfigurationTests
    {
        [Fact]
        public void ShouldEnableSafeTrailerDefaults()
        {
            var configuration = new PluginConfiguration();

            Assert.True(configuration.EnableTrailers);
            Assert.Equal(5, configuration.MaximumTrailers);
            Assert.True(configuration.PreferOfficialTrailers);
            Assert.True(configuration.PreferRussianTrailers);
            Assert.True(configuration.IncludeTrailerTeasers);
            Assert.False(configuration.IncludeAdditionalTrailerVideos);
            Assert.True(configuration.PrefixTrailerNames);
        }

        [Theory]
        [InlineData(-100, 1)]
        [InlineData(0, 1)]
        [InlineData(1, 1)]
        [InlineData(10, 10)]
        [InlineData(20, 20)]
        [InlineData(100, 20)]
        public void ShouldNormalizeMaximumTrailerCount(int configured, int expected)
        {
            var configuration = new PluginConfiguration
            {
                MaximumTrailers = configured
            };

            configuration.Normalize();

            Assert.Equal(expected, configuration.MaximumTrailers);
        }
    }
}
