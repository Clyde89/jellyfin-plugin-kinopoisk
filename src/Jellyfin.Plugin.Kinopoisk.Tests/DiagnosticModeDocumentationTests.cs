using System;
using System.IO;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class DiagnosticModeDocumentationTests
    {
        [Fact]
        public void ShouldDocumentSafeDiagnosticFileHandling()
        {
            var root = FindRepositoryRoot();
            var path = Path.Combine(root, "docs", "diagnostic-test-mode.md");
            var text = File.ReadAllText(path);

            Assert.Contains("API-токен и заголовки авторизации не записаны", text);
            Assert.Contains("kinopoisk-diagnostic-<session-id>.jsonl", text);
            Assert.Contains("Apply-задача", text);
        }

        private static string FindRepositoryRoot()
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory is not null)
            {
                if (Directory.Exists(Path.Combine(directory.FullName, ".github"))
                    && Directory.Exists(Path.Combine(directory.FullName, "src")))
                {
                    return directory.FullName;
                }

                directory = directory.Parent;
            }

            throw new DirectoryNotFoundException("Корень репозитория не найден.");
        }
    }
}
