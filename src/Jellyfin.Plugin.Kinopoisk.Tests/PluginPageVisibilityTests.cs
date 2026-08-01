using System.Linq;
using System.Runtime.CompilerServices;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PluginPageVisibilityTests
    {
        [Fact]
        public void ShouldExposeDiagnosticPageInPluginMenu()
        {
            var plugin = (global::Jellyfin.Plugin.Kinopoisk.Plugin)
                RuntimeHelpers.GetUninitializedObject(
                    typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin));

            var pages = plugin.GetPages().ToArray();
            var diagnostics = Assert.Single(
                pages,
                page => page.Name == "КиноПоиск — диагностика");

            Assert.True(diagnostics.EnableInMainMenu);
            Assert.Equal("КиноПоиск — диагностика (Debug)", diagnostics.DisplayName);
            Assert.Equal("bug_report", diagnostics.MenuIcon);
        }
    }
}
