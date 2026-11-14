using System.Reflection;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using MediaBrowser.Common.Api;
using MediaBrowser.Controller.Library;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using TorviaSearch.Services;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Api;

/// <summary>
/// Search &amp; Add API for PHOENIXA and the web page. Every call needs a signed-in Jellyfin user,
/// and that user must be an admin or on the allowed list in the plugin settings.
/// </summary>
[ApiController]
[Route("TorviaSearch")]
[Authorize]
public class SearchController : ControllerBase
{
    private readonly SearchService _svc;
    private readonly IUserManager _users;
    private readonly ILogger<SearchController> _log;

    /// <summary>Initializes a new instance of the <see cref="SearchController"/> class.</summary>
    public SearchController(SearchService svc, IUserManager users, ILogger<SearchController> log)
    {
        _svc = svc;
        _users = users;
        _log = log;
    }

    /// <summary>
    /// What an app calls first. If this returns 404 the plugin isn't installed; if "allowed" is false,
    /// hide the Search &amp; Add button for this user.
    /// </summary>
    [HttpGet("Status")]
    public ActionResult Status()
    {
        var (id, admin) = Who();
        var cfg = Plugin.Instance!.Configuration;
        var allowed = Allowed(id, admin);
        var ready = !string.IsNullOrWhiteSpace(cfg.TmdbApiKey) && TorBoxApi.ResolveKey() != null;
        return J(new
        {
            installed = true,
            userName = id == Guid.Empty ? null : _users.GetUserById(id)?.Username,
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            allowed,
            ready,
            enabled = allowed && ready,
            isAdmin = admin,
            torviaInstalled = true,
            tags = SearchService.TagList(),
            movieTag = cfg.MovieTag,
            tvTag = cfg.TvTag,
            remainingToday = allowed ? _svc.Remaining(id, admin) : 0,
            dailyLimit = admin ? 0 : cfg.DailyLimitPerUser,
            buttonLabel = "Search & Add",
            ownTorrents = allowed && (admin || cfg.AllowOwnTorrents),
        });
    }

    /// <summary>Search by name, TMDB number, TMDB link, IMDb id (tt…) or IMDb link.</summary>
    [HttpGet("Search")]
    public async Task<ActionResult> Search([FromQuery] string query, CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        return await Run(async () => J(new { results = await _svc.SearchAsync(query ?? string.Empty, ct).ConfigureAwait(false) })).ConfigureAwait(false);
    }

    /// <summary>A movie or show: IMDb id, seasons with episode counts, and Jellyfin item id if already there.</summary>
    [HttpGet("Title/{type}/{tmdbId:int}")]
    public async Task<ActionResult> Title([FromRoute] string type, [FromRoute] int tmdbId, CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        return await Run(async () =>
        {
            var details = await _svc.DetailsAsync(Norm(type), tmdbId, ct).ConfigureAwait(false);
            return details == null ? J(new { error = "Not found on TMDB." }, 404) : J(details);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Releases already cached on TorBox, "Best" first.
    /// Movie: ?type=movie&amp;tmdbId=…  TV: ?type=tv&amp;tmdbId=…&amp;season=1&amp;episode=5, or &amp;wholeSeason=true for season packs.
    /// </summary>
    [HttpGet("Releases")]
    public async Task<ActionResult> Releases(
        [FromQuery] string type,
        [FromQuery] int tmdbId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] bool wholeSeason,
        [FromQuery] string? quality,
        [FromQuery] bool? tenBit,
        [FromQuery] bool? hdr,
        [FromQuery] string? lang,
        [FromQuery] bool? hideCam,
        [FromQuery] string? sort,
        [FromQuery] string? kind,
        CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        return await Run(async () =>
        {
            var (details, all) = await _svc.ReleasesAsync(Norm(type), tmdbId, season, episode, wholeSeason, ct).ConfigureAwait(false);
            var filter = new ReleaseFilter
            {
                Qualities = Split(quality),
                TenBit = tenBit ?? false,
                Hdr = hdr ?? false,
                Languages = Split(lang),
                HideCam = hideCam ?? false,
                Sort = sort,
                Kind = kind,
            };
            var releases = ReleaseFilter.Apply(all, filter);
            return J(new
            {
                totalCount = all.Count,
                available = ReleaseFilter.Facets(all),
                title = details.Title,
                imdbId = details.ImdbId,
                jellyfinItemId = details.JellyfinItemId,
                season,
                episode = wholeSeason ? null : episode,
                wholeSeason,
                count = releases.Count,
                releases,
            });
        }).ConfigureAwait(false);
    }

    /// <summary>Adds a release from the last Releases list: body { "hash": "…", "tag": "movies" }.</summary>
    [HttpPost("Add")]
    public async Task<ActionResult> Add([FromBody] JsonElement json, CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        // Accept "hash"/"Hash" and "tag"/"Tag".
        var body = new AddRequest();
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in json.EnumerateObject())
            {
                if (p.Value.ValueKind != JsonValueKind.String)
                {
                    continue;
                }

                if (p.Name.Equals("hash", StringComparison.OrdinalIgnoreCase))
                {
                    body.Hash = p.Value.GetString() ?? string.Empty;
                }
                else if (p.Name.Equals("tag", StringComparison.OrdinalIgnoreCase))
                {
                    body.Tag = p.Value.GetString();
                }
            }
        }

        var (id, admin) = Who();
        var name = _users.GetUserById(id)?.Username ?? id.ToString("N");
        return await Run(async () =>
        {
            var r = await _svc.AddAsync(id, name, admin, body, ct).ConfigureAwait(false);
            return J(r, r.Ok ? 200 : 400);
        }).ConfigureAwait(false);
    }

    /// <summary>
    /// Adds the user's own magnet link or .torrent file under a title picked from TMDB:
    /// body { "magnet": "magnet:?…" or "file": "(base64 .torrent)", "type": "movie"|"tv", "tmdbId": 123, "tag": "movies" }.
    /// If TorBox doesn't have it cached, "downloading" is true and it appears in Jellyfin when the download is done.
    /// </summary>
    [HttpPost("AddOwn")]
    [RequestSizeLimit(12 * 1024 * 1024)]
    public async Task<ActionResult> AddOwn([FromBody] JsonElement json, CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        var body = new OwnAddRequest();
        if (json.ValueKind == JsonValueKind.Object)
        {
            foreach (var p in json.EnumerateObject())
            {
                var text = p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : null;
                switch (p.Name.ToLowerInvariant())
                {
                    case "magnet": body.Magnet = text; break;
                    case "file": body.File = text; break;
                    case "type": body.Type = Norm(text); break;
                    case "tag": body.Tag = text; break;
                    case "tmdbid":
                        body.TmdbId = p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetInt32(out var n) ? n
                            : int.TryParse(text, out var m) ? m : 0;
                        break;
                    default: break;
                }
            }
        }

        var (id, admin) = Who();
        var name = _users.GetUserById(id)?.Username ?? id.ToString("N");
        return await Run(async () =>
        {
            var r = await _svc.AddOwnAsync(id, name, admin, body, ct).ConfigureAwait(false);
            return J(r, r.Ok ? 200 : 400);
        }).ConfigureAwait(false);
    }

    /// <summary>Jellyfin item id for a TMDB id once Torvia has synced it (poll after an add). 404 until it's there.</summary>
    [HttpGet("InLibrary/{type}/{tmdbId:int}")]
    public ActionResult InLibrary([FromRoute] string type, [FromRoute] int tmdbId)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        var item = _svc.FindInLibrary(Norm(type), tmdbId);
        return item == null ? J(new { found = false }, 404) : J(new { found = true, itemId = item.Value.ToString("N") });
    }

    /// <summary>Last adds, for the admin page.</summary>
    [HttpGet("History")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult History() => J(new
    {
        items = _svc.Store.All().Take(100).Select(r => new { when = r.When, user = r.UserName, title = r.Name, tag = r.Tag }),
    });

    /// <summary>
    /// Titles added through Search &amp; Add. Users get their own; admins can pass all=true
    /// (and optionally user=&lt;id&gt;) to see everyone's.
    /// </summary>
    [HttpGet("Adds")]
    public ActionResult Adds([FromQuery] bool all, [FromQuery] string? user)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        var (id, admin) = Who();
        var mine = id.ToString("N");
        var records = _svc.Store.All();
        var everyone = admin && all;
        var users = everyone
            ? records.GroupBy(r => r.UserId).Select(g => new { id = g.Key, name = g.First().UserName, count = g.Count() }).OrderBy(u => u.name).ToList()
            : null;
        IEnumerable<AddRecord> list = everyone ? records : records.Where(r => r.UserId == mine);
        if (everyone && !string.IsNullOrWhiteSpace(user))
        {
            var want = user.Replace("-", string.Empty, StringComparison.Ordinal).ToLowerInvariant();
            list = list.Where(r => r.UserId == want);
        }

        return J(new
        {
            isAdmin = admin,
            all = everyone,
            users,
            items = list.Select(r => new
            {
                r.Id,
                r.UserId,
                r.UserName,
                r.When,
                r.Type,
                r.TmdbId,
                r.Title,
                r.Year,
                r.Poster,
                r.Season,
                r.Episode,
                r.Label,
                r.Quality,
                r.Size,
                r.Tag,
                r.Name,
                r.Release,
                r.AlreadyInTorBox,
                canRemove = admin || r.UserId == mine,
                jellyfinItemId = _svc.FindInLibrary(r.Type, r.TmdbId)?.ToString("N"),
                downloadPercent = _svc.DownloadPercent(r.TorrentId),
            }),
        });
    }

    /// <summary>Removes an add (own adds; admins: anyone's). Deletes it from TorBox when nobody else needs it.</summary>
    [HttpDelete("Adds/{id}")]
    public async Task<ActionResult> RemoveAdd([FromRoute] string id, CancellationToken ct)
    {
        if (Deny() is { } d)
        {
            return d;
        }

        var (uid, admin) = Who();
        return await Run(async () =>
        {
            var (status, message, deleted) = await _svc.RemoveAsync(id, uid, admin, ct).ConfigureAwait(false);
            return J(new { ok = status == 200, message, deletedFromTorBox = deleted }, status);
        }).ConfigureAwait(false);
    }

    /// <summary>The Search &amp; Add web page (open it in a browser where you're signed in to Jellyfin).</summary>
    [HttpGet("Web")]
    [AllowAnonymous]
    public ActionResult Web()
    {
        using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("TorviaSearch.Web.search.html");
        if (s == null)
        {
            return NotFound();
        }

        using var reader = new StreamReader(s);
        return Content(reader.ReadToEnd(), "text/html; charset=utf-8");
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>All answers are camelCase JSON.</summary>
    private static JsonResult J(object value, int status = 200) => new(value, JsonOpts) { StatusCode = status };

    private static List<string> Split(string? s) =>
        (s ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Where(x => !x.Equals("all", StringComparison.OrdinalIgnoreCase) && !x.Equals("any", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string Norm(string? type) =>
        string.Equals(type, "tv", StringComparison.OrdinalIgnoreCase) || string.Equals(type, "series", StringComparison.OrdinalIgnoreCase) ? "tv" : "movie";

    private (Guid Id, bool Admin) Who()
    {
        var raw = User.FindFirstValue("Jellyfin-UserId");
        Guid.TryParse(raw, out var id);
        var admin = User.IsInRole("Administrator");
        return (id, admin);
    }

    private static bool Allowed(Guid id, bool admin)
    {
        if (admin)
        {
            return true;
        }

        var cfg = Plugin.Instance!.Configuration;
        if (id == Guid.Empty)
        {
            return false;
        }

        return cfg.AllowAllUsers || cfg.AllowedUserIds.Any(a => Guid.TryParse(a, out var g) && g == id);
    }

    private ActionResult? Deny()
    {
        var (id, admin) = Who();
        return Allowed(id, admin) ? null : J(new { error = "Search & Add isn't turned on for your account. Ask the server owner." }, 403);
    }

    private async Task<ActionResult> Run(Func<Task<ActionResult>> work)
    {
        try
        {
            return await work().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _log.LogWarning("Torvia Search: {Msg}", ex.Message);
            // 4xx, not 5xx: Jellyfin (or a proxy in front) replaces 5xx answers with a generic text,
            // and the real reason would be lost.
            return J(new { error = ex.Message, message = ex.Message }, 409);
        }
    }
}
