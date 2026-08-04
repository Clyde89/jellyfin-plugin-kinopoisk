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
        public void ShouldRegisterTagLocalizationHostedService()
        {
            var source = File.ReadAllText(
                Path.Combine(
                    AppContext.BaseDirectory,
                    "..",
                    "..",
                    "..",
                    "..",
                    "Jellyfin.Plugin.Kinopoisk",
                    "KinopoiskPluginServiceRegistrator.cs"));

            Assert.Contains(
                "AddHostedService<KinopoiskWebTagLocalizationService>()",
                source);
        }
    }
}
