using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskTagLocalizationTests
    {
        [Fact]
        public void ShouldEmbedDisplayOnlyTagLocalization()
        {
            const string resourceName =
                "Jellyfin.Plugin.Kinopoisk.Web.kinopoiskTagLocalization.js";
            var assembly = typeof(global::Jellyfin.Plugin.Kinopoisk.Plugin).Assembly;

            Assert.Contains(resourceName, assembly.GetManifestResourceNames());
            using var stream = assembly.GetManifestResourceStream(resourceName);
            Assert.NotNull(stream);
            using var reader = new StreamReader(stream!);
            var script = reader.ReadToEnd();

            Assert.Contains(".itemTags", script);
            Assert.Contains("cloneNode(false)", script);
            Assert.Contains("kpOriginalTag", script);
            Assert.Contains("Темы и теги", script);
            Assert.Contains("survival horror", script);
            Assert.Contains("хоррор на выживание", script);
            Assert.Contains("unknown threat", script);
            Assert.Contains("неизвестная угроза", script);
            Assert.DoesNotContain("setAttribute('href'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("ProviderIds", script, StringComparison.Ordinal);
            Assert.DoesNotContain("X-API-KEY", script, StringComparison.OrdinalIgnoreCase);
        }

        [Fact]
        public void ShouldRegisterTagLocalizationInAutonomousBundle()
        {
            var projectDirectory = Path.Combine(
                AppContext.BaseDirectory,
                "..",
                "..",
                "..",
                "..",
                "Jellyfin.Plugin.Kinopoisk");
            var registrator = File.ReadAllText(
                Path.Combine(projectDirectory, "KinopoiskPluginServiceRegistrator.cs"));
            var bundle = File.ReadAllText(
                Path.Combine(
                    projectDirectory,
                    "Services",
                    "KinopoiskWebClientBundle.cs"));

            Assert.Contains(
                "AddSingleton<IStartupFilter, KinopoiskWebBootstrapStartupFilter>()",
                registrator);
            Assert.Contains("kinopoiskTagLocalization.js", bundle);
        }
    }
}
