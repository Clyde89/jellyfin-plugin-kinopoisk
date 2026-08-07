using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskWebBootstrapMiddlewareTests
    {
        private const string OriginalHtml =
            "<!doctype html><html><body><main>Jellyfin</main></body></html>";

        [Theory]
        [InlineData("/web/")]
        [InlineData("/web/index.html")]
        [InlineData("/jellyfin/web/")]
        [InlineData("/jellyfin/web/index.html")]
        public async Task ShouldTransformSupportedIndexPaths(string path)
        {
            var state = new KinopoiskWebBootstrapState();
            var middleware = CreateMiddleware(state, OriginalHtml);
            var context = CreateContext(path);

            await middleware.InvokeAsync(context);

            var body = await ReadResponseAsync(context);
            Assert.Equal(StatusCodes.Status200OK, context.Response.StatusCode);
            Assert.Contains(
                KinopoiskWebBootstrapTransformer.RuntimeManagedAttribute,
                body,
                StringComparison.Ordinal);
            Assert.Contains("<main>Jellyfin</main>", body, StringComparison.Ordinal);
            Assert.Equal("runtime", context.Response.Headers["X-Kinopoisk-Web-Bootstrap"]);
            Assert.StartsWith("\"kp-", context.Response.Headers.ETag.ToString());
            Assert.Equal(1, state.TransformedResponses);
            Assert.Equal(0, state.Failures);
        }

        [Fact]
        public async Task ShouldLeaveUnrelatedRouteUntouched()
        {
            var state = new KinopoiskWebBootstrapState();
            var middleware = CreateMiddleware(state, OriginalHtml);
            var context = CreateContext("/Items/123");
            context.Request.Headers.AcceptEncoding = "gzip";

            await middleware.InvokeAsync(context);

            Assert.Equal(OriginalHtml, await ReadResponseAsync(context));
            Assert.Equal("gzip", context.Request.Headers.AcceptEncoding.ToString());
            Assert.False(context.Response.Headers.ContainsKey("X-Kinopoisk-Web-Bootstrap"));
            Assert.Equal(0, state.TransformedResponses);
        }

        [Fact]
        public async Task ShouldRestoreRequestHeadersAndReturn304ForRuntimeEtag()
        {
            var state = new KinopoiskWebBootstrapState();
            var middleware = CreateMiddleware(state, OriginalHtml);
            var first = CreateContext("/web/");

            await middleware.InvokeAsync(first);
            var etag = first.Response.Headers.ETag.ToString();

            var second = CreateContext("/web/");
            second.Request.Headers.AcceptEncoding = "gzip, br";
            second.Request.Headers.IfNoneMatch = etag;
            second.Request.Headers.IfModifiedSince = "Wed, 21 Oct 2015 07:28:00 GMT";

            await middleware.InvokeAsync(second);

            Assert.Equal(StatusCodes.Status304NotModified, second.Response.StatusCode);
            Assert.Equal(0, second.Response.Body.Length);
            Assert.Equal("gzip, br", second.Request.Headers.AcceptEncoding.ToString());
            Assert.Equal(etag, second.Request.Headers.IfNoneMatch.ToString());
            Assert.Equal(
                "Wed, 21 Oct 2015 07:28:00 GMT",
                second.Request.Headers.IfModifiedSince.ToString());
            Assert.Equal(etag, second.Response.Headers.ETag.ToString());
        }

        [Fact]
        public async Task ShouldMigrateLegacyExternalBlockWithoutDuplication()
        {
            var external = KinopoiskStandaloneWebClientService.BuildExternallyManagedIndex(
                OriginalHtml);
            var state = new KinopoiskWebBootstrapState();
            var middleware = CreateMiddleware(state, external);
            var context = CreateContext("/web/");

            await middleware.InvokeAsync(context);

            var body = await ReadResponseAsync(context);
            Assert.Equal(
                1,
                KinopoiskWebBootstrapTransformer.CountOccurrences(
                    body,
                    KinopoiskWebBootstrapTransformer.WebClientPath));
            Assert.DoesNotContain(
                KinopoiskStandaloneWebClientService.ExternalManagedAttribute,
                body,
                StringComparison.Ordinal);
            Assert.Contains(
                KinopoiskWebBootstrapTransformer.RuntimeManagedAttribute,
                body,
                StringComparison.Ordinal);
        }

        [Fact]
        public async Task ShouldFailOpenForMalformedManagedBlock()
        {
            var malformed = string.Concat(
                "<html><body>",
                KinopoiskWebBootstrapTransformer.BeginMarker,
                "</body></html>");
            var state = new KinopoiskWebBootstrapState();
            var middleware = CreateMiddleware(state, malformed);
            var context = CreateContext("/web/");

            var exception = await Record.ExceptionAsync(() => middleware.InvokeAsync(context));

            Assert.Null(exception);
            Assert.Equal(malformed, await ReadResponseAsync(context));
            Assert.Equal(0, state.TransformedResponses);
            Assert.Equal(1, state.Failures);
        }

        private static KinopoiskWebBootstrapMiddleware CreateMiddleware(
            KinopoiskWebBootstrapState state,
            string html)
        {
            return new KinopoiskWebBootstrapMiddleware(
                async context =>
                {
                    context.Response.StatusCode = StatusCodes.Status200OK;
                    context.Response.ContentType = "text/html; charset=utf-8";
                    var bytes = Encoding.UTF8.GetBytes(html);
                    context.Response.ContentLength = bytes.Length;
                    await context.Response.Body.WriteAsync(bytes);
                },
                state,
                NullLogger<KinopoiskWebBootstrapMiddleware>.Instance);
        }

        private static DefaultHttpContext CreateContext(string path)
        {
            var context = new DefaultHttpContext();
            context.Request.Method = HttpMethods.Get;
            context.Request.Path = path;
            context.Response.Body = new MemoryStream();
            return context;
        }

        private static async Task<string> ReadResponseAsync(HttpContext context)
        {
            context.Response.Body.Position = 0;
            using var reader = new StreamReader(
                context.Response.Body,
                Encoding.UTF8,
                detectEncodingFromByteOrderMarks: false,
                leaveOpen: true);
            return await reader.ReadToEndAsync();
        }
    }
}
