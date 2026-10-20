#nullable enable

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Data.Enums;
using Jellyfin.Plugin.Kinopoisk.Services;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kinopoisk.Api;

[ApiController]
[Authorize]
[Route("KinopoiskRecommendations")]
public sealed class KinopoiskRecommendationActionsController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IUserDataManager _userData;
    private readonly KinopoiskWishlistStore _wishlist;
    public KinopoiskRecommendationActionsController(ILibraryManager library, IUserManager users,
        IUserDataManager userData, KinopoiskWishlistStore wishlist)
    { _library = library; _users = users; _userData = userData; _wishlist = wishlist; }

    [HttpGet("state")]
    public Task<IActionResult> State([FromQuery] string keys, CancellationToken token) => Execute(async id =>
    {
        if (string.IsNullOrEmpty(keys) || keys.Length > 4000) return BadRequest();
        var entries = keys.Split(',').Distinct().Select(KinopoiskWishlistItem.ParseKey).ToArray();
        if (entries.Length is < 1 or > 50) return BadRequest();
        var wishes = await _wishlist.Read(id, token).ConfigureAwait(false);
        var user = _users.GetUserById(id)!;
        var items = entries.Select(entry =>
        {
            var matches = _library.GetItemList(new InternalItemsQuery(user)
            {
                Recursive = true,
                IncludeItemTypes = new[] { entry.MediaType == "tv" ? BaseItemKind.Series : BaseItemKind.Movie },
                HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                { [entry.KinopoiskId > 0 ? Constants.ProviderId : "Tmdb"] = (entry.KinopoiskId > 0 ? entry.KinopoiskId : entry.TmdbId).ToString() },
                EnableTotalRecordCount = false
            }).Where(item => item.IsVisibleStandalone(user)).Take(2).ToArray();
            var item = matches.Length == 1 ? matches[0] : null;
            var data = item is null ? null : _userData.GetUserData(user, item);
            var canPlay = item is not null && item.GetPlayAccess(user) == PlayAccess.Full && !item.IsVirtualItem;
            if (item is Series)
            {
                canPlay &= _library.GetItemList(new InternalItemsQuery(user)
                { ParentId = item.Id, Recursive = true, IncludeItemTypes = new[] { BaseItemKind.Episode },
                    IsVirtualItem = false, IsPlaceHolder = false, Limit = 1, EnableTotalRecordCount = false })
                    .Any(episode => episode.IsVisibleStandalone(user) && !string.IsNullOrWhiteSpace(episode.Path));
            }
            else canPlay &= item is Video video && !video.IsPlaceHolder && !string.IsNullOrWhiteSpace(video.Path);
            return new { key = entry.Key, itemId = item?.Id.ToString("N"), canPlay,
                favorite = data?.IsFavorite ?? false, played = data?.Played ?? false,
                playbackPositionTicks = data?.PlaybackPositionTicks ?? 0, runTimeTicks = item?.RunTimeTicks ?? 0,
                wished = wishes.Any(wish => wish.Matches(entry)) };
        }).ToArray();
        return Ok(new { items });
    });

    [HttpGet("wishlist")]
    public Task<IActionResult> List(CancellationToken token) => Execute(async id =>
        Ok(new { items = await _wishlist.Read(id, token).ConfigureAwait(false) }));

    [HttpPut("wishlist")]
    [RequestSizeLimit(16384)]
    public Task<IActionResult> Save([FromBody] KinopoiskWishlistItem item, CancellationToken token) => Execute(async id =>
    {
        item.Validate(true);
        if (!string.IsNullOrEmpty(item.PosterUrl)
            && !KinopoiskPresentationImageUrlValidator.TryNormalize(item.PosterUrl, out _)) item.PosterUrl = string.Empty;
        await _wishlist.Set(id, item, true, token).ConfigureAwait(false);
        return Ok(new { saved = true });
    });

    [HttpDelete("wishlist/{mediaType}/{provider}/{id:int}")]
    public Task<IActionResult> Remove(string mediaType, string provider, int id, CancellationToken token) => Execute(async user =>
    {
        await _wishlist.Set(user, KinopoiskWishlistItem.ParseKey(mediaType + ":" + provider + ":" + id), false, token).ConfigureAwait(false);
        return Ok(new { saved = false });
    });

    private async Task<IActionResult> Execute(Func<Guid, Task<IActionResult>> action)
    {
        Response.Headers.CacheControl = "no-store";
        if (!Guid.TryParse(User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value, out var id)
            || id == Guid.Empty || _users.GetUserById(id) is null) return Forbid();
        try { return await action(id).ConfigureAwait(false); }
        catch (ArgumentException) { return BadRequest(new { message = "Проверьте данные фильма или сериала." }); }
        catch (InvalidOperationException error) { return Conflict(new { message = error.Message }); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException)
        { return StatusCode(503, new { message = "Не удалось прочитать или сохранить желаемое. Повторите попытку позже." }); }
    }
}
