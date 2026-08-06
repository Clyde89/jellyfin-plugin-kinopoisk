using System;
using Jellyfin.Plugin.Kinopoisk.Api;
using Jellyfin.Plugin.Kinopoisk.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests
{
    public class KinopoiskWebClientControllerTests
    {
        [Fact]
        public void ShouldReturnVersionedBundleWithImmutableCache()
        {
            var controller = CreateController();
            controller.Request.QueryString = new QueryString(
                string.Concat("?v=", KinopoiskWebClientBundle.VersionToken));

            var result = Assert.IsType<FileContentResult>(controller.GetWebClient());

            Assert.Equal("application/javascript; charset=utf-8", result.ContentType);
            Assert.Equal(KinopoiskWebClientBundle.Bytes, result.FileContents);
            Assert.Equal(
                "public, max-age=31536000, immutable",
                controller.Response.Headers.CacheControl.ToString());
            Assert.Equal(
                string.Concat('"', KinopoiskWebClientBundle.Sha256, '"'),
                controller.Response.Headers.ETag.ToString());
            Assert.Equal(
                "nosniff",
                controller.Response.Headers["X-Content-Type-Options"].ToString());
        }

        [Fact]
        public void ShouldDisableLongCacheWithoutMatchingVersion()
        {
            var controller = CreateController();

            _ = Assert.IsType<FileContentResult>(controller.GetWebClient());

            Assert.Equal("no-cache", controller.Response.Headers.CacheControl.ToString());
        }

        [Fact]
        public void ShouldReturnNotModifiedForMatchingEtag()
        {
            var controller = CreateController();
            controller.Request.Headers.IfNoneMatch = string.Concat(
                '"',
                KinopoiskWebClientBundle.Sha256,
                '"');

            var result = Assert.IsType<StatusCodeResult>(controller.GetWebClient());

            Assert.Equal(StatusCodes.Status304NotModified, result.StatusCode);
        }

        private static KinopoiskWebClientController CreateController()
            => new()
            {
                ControllerContext = new ControllerContext
                {
                    HttpContext = new DefaultHttpContext()
                }
            };
    }
}
