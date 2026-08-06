using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PluginPageVisibilityTests
    {
        [Fact]
        public void ShouldExposeSingleUnifiedPluginPage()
        {
            var plugin = (global::Jellyfin.Plugin.Kinopoisk.Plugin)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin));

            var page = Assert.Single(plugin.GetPages());

            Assert.Equal("КиноПоиск", page.Name);
            Assert.Equal("КиноПоиск", page.DisplayName);
            Assert.True(page.EnableInMainMenu);
            Assert.Equal("settings", page.MenuIcon);
            Assert.EndsWith("Configuration.configPage.html", page.EmbeddedResourcePath);
        }
    }
}
