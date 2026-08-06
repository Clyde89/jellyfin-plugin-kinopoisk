using System.Reflection;
using Jellyfin.Plugin.Kinopoisk.Services;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class DiagnosticLoggerSanitizationTests
    {
        [Fact]
        public void ShouldMaskCompleteBearerAuthorizationHeader()
        {
            const string secret = "eyJhbGciOiJIUzI1NiJ9.payload.signature";

            var sanitized = Sanitize(
                $"Ошибка запроса; Authorization: Bearer {secret}; получен HTTP 401");

            Assert.DoesNotContain(secret, sanitized);
            Assert.Contains("authorization=***", sanitized.ToLowerInvariant());
            Assert.Contains("получен HTTP 401", sanitized);
        }

        [Theory]
        [InlineData("X-Api-Key: private-api-key", "private-api-key")]
        [InlineData("ApiToken=private-token", "private-token")]
        [InlineData("token: private-token", "private-token")]
        public void ShouldMaskNamedSecrets(string source, string secret)
        {
            var sanitized = Sanitize(source);

            Assert.DoesNotContain(secret, sanitized);
            Assert.Contains("***", sanitized);
        }

        [Fact]
        public void ShouldRemoveQueryParametersFromUrls()
        {
            const string secret = "private-query-token";

            var sanitized = Sanitize(
                $"Запрос https://kinopoiskapiunofficial.tech/api/v2.2/films?token={secret}&page=1 завершён");

            Assert.DoesNotContain(secret, sanitized);
            Assert.DoesNotContain("?token=", sanitized);
            Assert.Contains("https://kinopoiskapiunofficial.tech/api/v2.2/films", sanitized);
        }

        private static string Sanitize(string value)
        {
            var method = typeof(KinopoiskDiagnosticFileSink).GetMethod(
                "Sanitize",
                BindingFlags.NonPublic | BindingFlags.Static);

            Assert.NotNull(method);
            return Assert.IsType<string>(method.Invoke(null, new object[] { value }));
        }
    }
}
