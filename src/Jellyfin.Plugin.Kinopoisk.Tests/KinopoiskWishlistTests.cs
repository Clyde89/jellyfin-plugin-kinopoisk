using System;
using System.IO;
using System.Linq;
using System.Security.Claims;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Database.Implementations.Entities;
using Jellyfin.Database.Implementations.Enums;
using Jellyfin.Plugin.Kinopoisk.Api;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Moq;
using Xunit;

namespace Jellyfin.Plugin.Kinopoisk.Tests;

public sealed class KinopoiskWishlistTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "kinopoisk-wishlist-" + Guid.NewGuid().ToString("N"));
    private static readonly Guid First = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid Second = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");
    private static KinopoiskWishlistItem Entry(int id = 100) => new() { KinopoiskId = id, MediaType = "movie", Name = "Сохранённый фильм", Year = 2025 };
    private KinopoiskWishlistStore Store() => new(_directory);

    [Fact]
    public async Task SavedTitlesSurviveRecreationAndRemainPrivateToUser()
    {
        await Store().Set(First, Entry(), true, CancellationToken.None);
        Assert.Single(await Store().Read(First, CancellationToken.None));
        Assert.Empty(await Store().Read(Second, CancellationToken.None));
        await Store().Set(First, Entry(), true, CancellationToken.None);
        Assert.Single(await Store().Read(First, CancellationToken.None));
        await Store().Set(First, Entry(), false, CancellationToken.None);
        Assert.Empty(await Store().Read(First, CancellationToken.None));
    }

    [Fact]
    public async Task ConcurrentChangesDoNotLoseOtherTitlesAndKeepPreviousVersion()
    {
        var store = Store();
        await Task.WhenAll(Enumerable.Range(1, 24).Select(id => store.Set(First, Entry(id), true, CancellationToken.None)));
        Assert.Equal(24, (await Store().Read(First, CancellationToken.None)).Count);
        Assert.True(File.Exists(Path.Combine(_directory, First.ToString("N") + ".json.previous")));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
    }

    [Fact]
    public async Task CorruptListIsNotOverwrittenWithEmptyState()
    {
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, First.ToString("N") + ".json");
        await File.WriteAllTextAsync(path, "damaged-list");
        await Assert.ThrowsAsync<JsonException>(() => Store().Set(First, Entry(), true, CancellationToken.None));
        Assert.Equal("damaged-list", await File.ReadAllTextAsync(path));
    }

    [Theory]
    [InlineData("movie:kp:0")]
    [InlineData("movie:kp:../outside")]
    [InlineData("person:kp:100")]
    [InlineData("movie:imdb:100")]
    public void InvalidIdentityCannotBecomeStoragePath(string key)
        => Assert.Throws<ArgumentException>(() => KinopoiskWishlistItem.ParseKey(key));

    [Fact]
    public async Task SameTitleWithDifferentTypesIsKeptSeparatelyAndKnownIdsAreMerged()
    {
        var item = Entry();
        item.TmdbId = 501;
        await Store().Set(First, item, true, CancellationToken.None);
        var series = Entry();
        series.MediaType = "tv";
        await Store().Set(First, series, true, CancellationToken.None);
        var alias = Entry();
        alias.KinopoiskId = 0;
        alias.TmdbId = 501;
        await Store().Set(First, alias, false, CancellationToken.None);
        Assert.Equal("tv", Assert.Single(await Store().Read(First, CancellationToken.None)).MediaType);
    }

    [Fact]
    public async Task ControllerUsesPrincipalRatherThanIdentityHeaders()
    {
        var users = new Mock<IUserManager>();
        var second = NewUser(Second);
        users.Setup(service => service.GetUserById(Second)).Returns(second);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Emby-UserId"] = Second.ToString();
        var controller = Controller(users, new Mock<ILibraryManager>(), new Mock<IUserDataManager>(), context);
        Assert.IsType<ForbidResult>(await controller.Save(Entry(), CancellationToken.None));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", Second.ToString()) }, "fixture"));
        Assert.IsType<OkObjectResult>(await controller.Save(Entry(), CancellationToken.None));
        Assert.Empty(await Store().Read(First, CancellationToken.None));
        Assert.Single(await Store().Read(Second, CancellationToken.None));
        Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [Fact]
    public async Task LibraryActionsRequireUniqueVisibleExactProviderIdAndUserContext()
    {
        var users = new Mock<IUserManager>();
        var user = NewUser(First);
        users.Setup(service => service.GetUserById(First)).Returns(user);
        var movie = new VisibleMovie { Id = Guid.NewGuid(), Path = "/media/movie.mkv", Name = "Фильм" };
        var library = new Mock<ILibraryManager>();
        library.Setup(service => service.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { movie });
        var userData = new Mock<IUserDataManager>();
        userData.Setup(service => service.GetUserData(user, movie)).Returns(new UserItemData { Key = "fixture-user-data", IsFavorite = true, PlaybackPositionTicks = 12345678 });
        var context = new DefaultHttpContext { User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim("Jellyfin-UserId", First.ToString()) }, "fixture")) };
        var controller = Controller(users, library, userData, context);
        var response = Json(await controller.State("movie:kp:100", CancellationToken.None)).GetProperty("items")[0];
        Assert.True(response.GetProperty("canPlay").GetBoolean());
        Assert.True(response.GetProperty("favorite").GetBoolean());
        Assert.Equal(12345678, response.GetProperty("playbackPositionTicks").GetInt64());
        library.Verify(service => service.GetItemList(It.Is<InternalItemsQuery>(query => query.User.Id == First
            && query.IncludeItemTypes.Single() == BaseItemKind.Movie && query.HasAnyProviderId[Constants.ProviderId] == "100")));
        library.Setup(service => service.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { movie, new VisibleMovie() });
        response = Json(await controller.State("movie:kp:100", CancellationToken.None)).GetProperty("items")[0];
        Assert.Equal(JsonValueKind.Null, response.GetProperty("itemId").ValueKind);
        movie.Visible = false;
        library.Setup(service => service.GetItemList(It.IsAny<InternalItemsQuery>())).Returns(new BaseItem[] { movie });
        response = Json(await controller.State("movie:tmdb:501", CancellationToken.None)).GetProperty("items")[0];
        Assert.False(response.GetProperty("canPlay").GetBoolean());
        Assert.Equal(JsonValueKind.Null, response.GetProperty("itemId").ValueKind);
    }

    private KinopoiskRecommendationActionsController Controller(Mock<IUserManager> users, Mock<ILibraryManager> library, Mock<IUserDataManager> data, DefaultHttpContext context)
        => new(library.Object, users.Object, data.Object, Store()) { ControllerContext = new ControllerContext { HttpContext = context } };
    private static JsonElement Json(IActionResult result) => JsonSerializer.SerializeToElement(Assert.IsType<OkObjectResult>(result).Value);
    private static User NewUser(Guid id)
    {
        var user = new User("fixture", "fixture", "fixture") { Id = id };
        user.SetPermission(PermissionKind.EnableMediaPlayback, true);
        return user;
    }
    private sealed class VisibleMovie : Movie
    {
        public bool Visible = true;
        public override bool IsVisibleStandalone(User user) => Visible;
    }
    public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
}
