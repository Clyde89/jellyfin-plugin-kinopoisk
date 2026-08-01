using System;
using System.IO;
using System.Linq;
using System.Xml.Linq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class PluginLogoPresentationTests
    {
        [Fact]
        public void ShouldUseWideSafeAreaAndRussianKpMark()
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, "assets", "kinopoisk-plugin.svg");
            var document = XDocument.Load(path);
            var svg = Assert.IsType<XElement>(document.Root);

            Assert.Equal("0 0 1024 576", svg.Attribute("viewBox")?.Value);
            Assert.Contains("КП", svg.Descendants().Single(item => item.Name.LocalName == "desc").Value);
            Assert.Equal(2, svg.Descendants().Count(item => item.Name.LocalName == "path"));
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".github"))
                    && Directory.Exists(Path.Combine(directory.FullName, "assets")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Корень репозитория не найден.");
        }
    }
}
