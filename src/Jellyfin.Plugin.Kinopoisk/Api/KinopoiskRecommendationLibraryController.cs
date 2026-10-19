#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace Jellyfin.Plugin.Kinopoisk.Api;

[ApiController]
[Authorize]
[Route("KinopoiskRecommendations/library")]
public sealed class KinopoiskRecommendationLibraryController : ControllerBase
{
    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    public KinopoiskRecommendationLibraryController(ILibraryManager library, IUserManager users)
    {
        _library = library;
        _users = users;
    }

    [HttpGet("{kinopoiskId:int}")]
    public IActionResult Find(int kinopoiskId, [FromQuery] string mediaType = "movie")
    {
        Response.Headers.CacheControl = "no-store";
        if (kinopoiskId < 1 || mediaType is not ("movie" or "tv")) return BadRequest();
        if (!Guid.TryParse(User.Claims.FirstOrDefault(c => c.Type == "Jellyfin-UserId")?.Value, out var userId))
            return Forbid();
        var user = _users.GetUserById(userId);
        if (user is null) return Forbid();
        // User context applies library access and parental restrictions to the lookup.
        var matches = _library.GetItemList(new InternalItemsQuery(user)
        {
            Recursive = true,
            IncludeItemTypes = new[] { mediaType == "tv" ? BaseItemKind.Series : BaseItemKind.Movie },
            HasAnyProviderId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [Constants.ProviderId] = kinopoiskId.ToString()
            },
            EnableTotalRecordCount = false
        }).Where(item => item.IsVisibleStandalone(user)).Take(2).ToArray();
        return Ok(new { itemId = matches.Length == 1 ? matches[0].Id.ToString("N") : null });
    }
}
