using System.Collections.Concurrent;
using Jellyfin.Data.Enums;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>The bot's recipe: find the title, get releases, keep the cached ones, add the pick, sync Torvia.</summary>
public class SearchService
{
    private const string TorviaSyncKey = "PhoenicxTorBoxSync";

    private readonly TmdbClient _tmdb;
    private readonly TorrentioClient _torrentio;
    private readonly TorBoxApi _torbox;
    private readonly ITaskManager _tasks;
    private readonly PhoenicxTorBox.Library.LibrarySync _sync;
    private readonly ILibraryManager _library;
    private readonly AddStore _store;
    private readonly ILogger<SearchService> _log;

    private readonly ConcurrentDictionary<string, PendingRelease> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _addLock = new(1, 1);

    /// <summary>Initializes a new instance of the <see cref="SearchService"/> class.</summary>
    public SearchService(TmdbClient tmdb, TorrentioClient torrentio, TorBoxApi torbox, ITaskManager tasks, ILibraryManager library, AddStore store, PhoenicxTorBox.Library.LibrarySync sync, ILogger<SearchService> log)
    {
        _sync = sync;
        _tmdb = tmdb;
        _torrentio = torrentio;
        _torbox = torbox;
        _tasks = tasks;
        _library = library;
        _store = store;
        _log = log;
    }

    /// <summary>Gets the saved adds.</summary>
    public AddStore Store => _store;

    /// <summary>Search by name / TMDB number / TMDB link / IMDb id.</summary>
    public Task<List<TitleResult>> SearchAsync(string query, CancellationToken ct) => _tmdb.SearchAsync(query, ct);

    /// <summary>Title details, seasons, and the Jellyfin item if it is already there.</summary>
    public async Task<TitleDetails?> DetailsAsync(string type, int tmdbId, CancellationToken ct)
    {
        var d = await _tmdb.GetDetailsAsync(type, tmdbId, ct).ConfigureAwait(false);
        if (d != null)
        {
            Remember(type, tmdbId, d);
            d.JellyfinItemId = FindInLibrary(type, tmdbId)?.ToString("N");
        }

        return d;
    }

    /// <summary>Finds an item in Jellyfin by TMDB id.</summary>
    public Guid? FindInLibrary(string type, int tmdbId)
    {
        var items = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { type == "tv" ? BaseItemKind.Series : BaseItemKind.Movie },
            HasAnyProviderId = new Dictionary<string, string> { [MetadataProvider.Tmdb.ToString()] = tmdbId.ToString(System.Globalization.CultureInfo.InvariantCulture) },
            Recursive = true,
            Limit = 1,
        });
        if (items.Count > 0)
        {
            return items[0].Id;
        }

        // Not matched by TMDB number (Jellyfin may have matched it another way, or is still
        // fetching details): look for the same name and year among Torvia's TorBox items.
        if (!_titles.TryGetValue(type + ":" + tmdbId, out var t) || Plugin.Instance is not { } plugin)
        {
            return null;
        }

        var want = PhoenicxTorBox.Library.NameParser.Normalize(t.Title);
        var root = plugin.LibraryRoot;
        foreach (var it in _library.GetItemList(new InternalItemsQuery
                 {
                     IncludeItemTypes = new[] { type == "tv" ? BaseItemKind.Series : BaseItemKind.Movie },
                     Recursive = true,
                 }))
        {
            if (it.Path is null || !it.Path.StartsWith(root, StringComparison.Ordinal))
            {
                continue;
            }

            var sameName = PhoenicxTorBox.Library.NameParser.Normalize(it.Name ?? string.Empty) == want
                || PhoenicxTorBox.Library.NameParser.Normalize(it.OriginalTitle ?? string.Empty) == want
                || PhoenicxTorBox.Library.NameParser.Normalize(System.IO.Path.GetFileName(it.Path.TrimEnd('/')).Split('(')[0]) == want;
            var sameYear = t.Year is null || it.ProductionYear is null || Math.Abs(it.ProductionYear.Value - t.Year.Value) <= 1;
            if (sameName && sameYear)
            {
                return it.Id;
            }
        }

        return null;
    }

    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, (string Title, int? Year)> _titles = new();

    private void Remember(string type, int tmdbId, TitleDetails d)
    {
        if (!string.IsNullOrWhiteSpace(d.Title.Title))
        {
            _titles[type + ":" + tmdbId] = (d.Title.Title, d.Title.Year);
        }
    }

    /// <summary>Gets the cached releases, sorted "Best" first.</summary>
    public async Task<(TitleDetails Details, List<ReleaseInfo> Releases)> ReleasesAsync(
        string type, int tmdbId, int? season, int? episode, bool wholeSeason, CancellationToken ct)
    {
        var d = await _tmdb.GetDetailsAsync(type, tmdbId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("TMDB doesn't know this title.");
        Remember(type, tmdbId, d);
        if (string.IsNullOrEmpty(d.ImdbId))
        {
            throw new InvalidOperationException("This title has no IMDb id on TMDB, so no releases can be found.");
        }

        var isTv = type == "tv";
        if (isTv)
        {
            season ??= 1;
            episode = wholeSeason ? 1 : episode ?? 1;
        }
        else
        {
            season = null;
            episode = null;
            wholeSeason = false;
        }

        var raw = await _torrentio.GetAsync(d.ImdbId!, season, episode, ct).ConfigureAwait(false);
        if (isTv)
        {
            raw = raw.Where(x => TvRelevant(x.Release, season!.Value, episode!.Value, wholeSeason)).ToList();
        }

        var cached = raw.Count == 0
            ? new Dictionary<string, long>()
            : await _torbox.CheckCachedAsync(raw.Select(x => x.Release.Hash), ct).ConfigureAwait(false);
        var showCam = Plugin.Instance?.Configuration.ShowCam ?? true;
        var list = new List<ReleaseInfo>();
        foreach (var (r, flags) in raw)
        {
            if (!cached.TryGetValue(r.Hash, out var packBytes))
            {
                continue;
            }

            r.PackSizeBytes = packBytes;
            if (wholeSeason && packBytes > 0)
            {
                r.SizeBytes = packBytes; // for a season, the whole pack is what matters
            }

            ReleaseParser.Parse(r, flags);
            if (packBytes > 0 && r.SizeBytes > 0 && packBytes > r.SizeBytes * 1.3)
            {
                r.PackSize = ReleaseParser.FormatSize(packBytes);
            }

            if (r.IsCam && !showCam)
            {
                continue;
            }

            list.Add(r);
        }

        list = ReleaseParser.SortBest(list);

        // Remember what was offered, so an add can only use a release this server found.
        var expires = DateTime.UtcNow.AddHours(2);
        foreach (var r in list)
        {
            _pending[r.Hash] = new PendingRelease
            {
                Release = r,
                Type = isTv ? "tv" : "movie",
                TmdbId = tmdbId,
                Title = d.Title.Title,
                Year = d.Title.Year,
                Poster = d.Title.Poster,
                Season = season,
                Episode = wholeSeason ? null : episode,
                WholeSeason = wholeSeason,
                Expires = expires,
            };
        }

        foreach (var old in _pending.Where(p => p.Value.Expires < DateTime.UtcNow).Select(p => p.Key).ToList())
        {
            _pending.TryRemove(old, out _);
        }

        d.JellyfinItemId = FindInLibrary(type, tmdbId)?.ToString("N");
        return (d, list);
    }

    /// <summary>
    /// Labels a TV release and says whether it really is for the asked season / episode.
    /// Names are read both from the torrent name and from the file Torrentio picked inside it.
    /// </summary>
    private static bool TvRelevant(ReleaseInfo r, int season, int episode, bool wholeSeason)
    {
        var name = TvParser.Parse(r.Name);
        var file = r.FileName == r.Name ? name : TvParser.Parse(r.FileName);

        // A file that clearly is another season or episode is a wrong match.
        if (file.Kind == "episode" && ((file.HasSeason && file.SeasonFrom != season) || (!wholeSeason && !file.CoversEpisode(episode))))
        {
            return false;
        }

        if (name.HasSeason && !name.CoversSeason(season))
        {
            return false;
        }

        var kind = name.Kind;
        if (kind == "unknown")
        {
            // No season/episode in the torrent name: a pack if the file is one episode of it.
            kind = file.Kind == "episode" ? "pack" : "unknown";
        }

        if (kind is "episode")
        {
            if (wholeSeason || !name.CoversEpisode(episode))
            {
                return false;
            }
        }
        else if (kind is "range")
        {
            if (!wholeSeason && !name.CoversEpisode(episode))
            {
                return false;
            }
        }
        else if (kind == "unknown" && wholeSeason)
        {
            return false;
        }

        r.Kind = kind;
        r.IsSeasonPack = kind is "range" or "season" or "multiseason" or "complete" or "pack";
        r.Label = kind == "pack" ? "Pack" : kind == "unknown" ? string.Empty : TvParser.Label(name);
        if (wholeSeason)
        {
            // the whole season is added; no single file to name
        }
        else if (r.IsSeasonPack && file.Kind == "episode")
        {
            r.PlaysLabel = $"S{season:00}E{(file.EpisodeFrom ?? episode):00}";
        }
        else if (r.IsSeasonPack && !wholeSeason)
        {
            r.PlaysLabel = $"S{season:00}E{episode:00}";
        }

        return true;
    }

    /// <summary>How many adds the user has left today, or null when there is no limit.</summary>
    public int? Remaining(Guid userId, bool isAdmin)
    {
        var limit = Plugin.Instance?.Configuration.DailyLimitPerUser ?? 0;
        if (isAdmin || limit <= 0)
        {
            return null;
        }

        return Math.Max(0, limit - _store.CountToday(userId));
    }

    /// <summary>Adds the pick to TorBox, tags it, gets a stream link and starts a Torvia sync.</summary>
    public async Task<AddResult> AddAsync(Guid userId, string userName, bool isAdmin, AddRequest req, CancellationToken ct)
    {
        var cfg = Plugin.Instance!.Configuration;
        var hash = (req.Hash ?? string.Empty).Trim().ToLowerInvariant();
        if (!_pending.TryGetValue(hash, out var p) || p.Expires < DateTime.UtcNow)
        {
            return new AddResult { Message = "This release list is old. Search again and pick it from the new list." };
        }

        var left = Remaining(userId, isAdmin);
        if (left == 0)
        {
            return new AddResult { Message = $"You've reached today's limit of {cfg.DailyLimitPerUser} adds. Try again tomorrow.", RemainingToday = 0 };
        }

        var allowedTags = TagList();
        var tag = string.IsNullOrWhiteSpace(req.Tag) ? (p.Type == "tv" ? cfg.TvTag : cfg.MovieTag) : req.Tag.Trim();
        if (!allowedTags.Contains(tag, StringComparer.OrdinalIgnoreCase))
        {
            return new AddResult { Message = "That tag isn't allowed." };
        }

        await _addLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var result = new AddResult { Tag = tag };

            // Movies get their title as the name; TV keeps the release name so Torvia can read S01E05.
            var name = p.Type == "movie"
                ? (p.Year is { } y ? $"{p.Title} ({y})" : p.Title)
                : p.Release.Name;
            result.Name = name;

            var existing = await _torbox.FindByHashAsync(hash, ct).ConfigureAwait(false);
            long id;
            if (existing is { } ex)
            {
                id = ex.Id;
                result.AlreadyInTorBox = true;
                if (!ex.Tags.Contains(tag, StringComparer.OrdinalIgnoreCase))
                {
                    await _torbox.SetTagsAsync(id, ex.Tags.Append(tag), ct).ConfigureAwait(false);
                }
            }
            else
            {
                // Check it is still cached right before adding.
                var still = await _torbox.CheckCachedAsync(new[] { hash }, ct).ConfigureAwait(false);
                if (!still.ContainsKey(hash))
                {
                    return new AddResult { Message = "TorBox no longer has this release cached. Pick another one." };
                }

                id = await _torbox.CreateAsync(hash, name, ct).ConfigureAwait(false);
                await _torbox.SetTagsAsync(id, new[] { tag }, ct).ConfigureAwait(false);
            }

            result.TorrentId = id;
            result.Ok = true;

            if (cfg.ReturnStreamLink)
            {
                try
                {
                    result.StreamUrl = await _torbox.GetStreamLinkAsync(id, p.Season, p.Episode ?? (p.Type == "tv" ? 1 : null), ct).ConfigureAwait(false);
                }
                catch (Exception e) when (e is not OperationCanceledException)
                {
                    _log.LogWarning("Torvia Search: no stream link yet for {Id}: {Msg}", id, e.Message);
                }
            }

            // Always bring it into Jellyfin right away.
            result.SyncStarted = StartTorviaSync();

            result.Message = (result.AlreadyInTorBox ? "Already in TorBox" : "Added to TorBox")
                + $" with tag “{tag}”."
                + (result.SyncStarted ? " It will appear in Jellyfin in a minute or two." : string.Empty);

            var rec = new AddRecord
            {
                UserId = userId.ToString("N"),
                UserName = userName,
                When = DateTime.UtcNow,
                Type = p.Type,
                TmdbId = p.TmdbId,
                Title = p.Title,
                Year = p.Year,
                Poster = p.Poster,
                Season = p.Season,
                Episode = p.Episode,
                Label = p.Type == "tv" ? (p.WholeSeason ? $"Season {p.Season}" : $"S{p.Season:00}E{p.Episode:00}") + (p.Release.IsSeasonPack && !p.WholeSeason ? " (" + p.Release.Label + ")" : string.Empty) : string.Empty,
                Quality = string.Join(" · ", new[] { p.Release.IsCam ? "CAM/TS" : p.Release.Source, p.Release.Quality == "?" ? string.Empty : p.Release.Quality, p.Release.Hdr == "SDR" ? string.Empty : p.Release.Hdr }.Where(x => x.Length > 0)),
                Size = p.Release.PackSize.Length > 0 ? p.Release.PackSize : p.Release.Size,
                Tag = tag,
                Name = name,
                Release = p.Release.Name,
                Hash = hash,
                TorrentId = id,
                AlreadyInTorBox = result.AlreadyInTorBox,
            };
            _store.Add(rec);
            result.RecordId = rec.Id;
            result.RemainingToday = Remaining(userId, isAdmin);

            _log.LogInformation("Torvia Search: {User} added {Name} [{Tag}] (torrent {Id})", userName, name, tag, id);
            return result;
        }
        finally
        {
            _addLock.Release();
        }
    }

    /// <summary>
    /// Removes an add: deletes it from TorBox (unless it was there before, or someone else added it too),
    /// forgets it, and syncs Torvia so it leaves Jellyfin.
    /// </summary>
    public async Task<(int Status, string Message, bool DeletedFromTorBox)> RemoveAsync(string recordId, Guid userId, bool isAdmin, CancellationToken ct)
    {
        var rec = _store.Get(recordId);
        if (rec == null)
        {
            return (404, "Not found. It may already be removed.", false);
        }

        if (!isAdmin && rec.UserId != userId.ToString("N"))
        {
            return (403, "You can only remove titles you added.", false);
        }

        await _addLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Normal users can't delete something the owner already had in TorBox, or that another
            // user still wants: it only leaves their list. Admins always delete it from TorBox.
            if (!isAdmin && rec.AlreadyInTorBox)
            {
                _store.Remove(rec.Id);
                return (200, $"Removed “{rec.Name}” from your list. It was already in TorBox before, so it was kept there.", false);
            }

            if (!isAdmin && _store.OthersUsing(rec) > 0)
            {
                _store.Remove(rec.Id);
                return (200, $"Removed “{rec.Name}” from your list. Someone else added it too, so it stays in TorBox.", false);
            }

            // Already deleted on TorBox directly? TorBox then answers with an error — that's fine,
            // the check below tells whether it's gone.
            string? deleteError = null;
            try
            {
                await _torbox.DeleteAsync(rec.TorrentId, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                deleteError = ex.Message;
            }

            // Check TorBox really let it go (wait a moment if it's slow).
            var stillThere = await _torbox.ExistsAsync(rec.TorrentId, ct).ConfigureAwait(false);
            if (stillThere)
            {
                await Task.Delay(2000, ct).ConfigureAwait(false);
                stillThere = await _torbox.ExistsAsync(rec.TorrentId, ct).ConfigureAwait(false);
            }

            if (stillThere)
            {
                _log.LogWarning("Torvia Search: TorBox did not delete torrent {Id} ({Name})", rec.TorrentId, rec.Name);
                return (409, $"TorBox didn't delete “{rec.Name}”" + (deleteError is null ? "." : $" ({deleteError}).") + " Try again in a minute.", false);
            }

            // Forget every list entry for this download (all users).
            foreach (var r in _store.All().Where(r => r.TorrentId == rec.TorrentId).ToList())
            {
                _store.Remove(r.Id);
            }

            // Always sync, so it leaves Jellyfin right away.
            var synced = StartTorviaSync();
            _log.LogInformation("Torvia Search: removed {Name} from TorBox (torrent {Id}, added by {User})", rec.Name, rec.TorrentId, rec.UserName);
            return (200, (deleteError is null ? $"Deleted “{rec.Name}” from TorBox." : $"“{rec.Name}” was already gone from TorBox; removed from the list.")
                + (synced ? " Torvia is syncing — it will leave Jellyfin in a minute." : string.Empty), deleteError is null);
        }
        finally
        {
            _addLock.Release();
        }
    }

    /// <summary>
    /// Runs Torvia's sync straight away in the background (Search is part of Torvia now, so no task
    /// queue in between). If a sync is already running, this one runs right after it, so the
    /// new title is always picked up.
    /// </summary>
    public bool StartTorviaSync()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // Give TorBox a moment to list the new download.
                await Task.Delay(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                var s = await _sync.RunAsync(null, CancellationToken.None).ConfigureAwait(false);
                _log.LogInformation("Torvia Search: sync after add/remove done: {Message}", s.Message);
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Torvia Search: sync after add/remove failed");
            }
        });
        return true;
    }

    /// <summary>Gets whether Torvia is installed.</summary>
    public bool TorviaInstalled => true;

    /// <summary>The tags users may pick.</summary>
    public static List<string> TagList()
    {
        var cfg = Plugin.Instance!.Configuration;
        var tags = (cfg.Tags ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToList();
        foreach (var t in new[] { cfg.MovieTag, cfg.TvTag })
        {
            if (!string.IsNullOrWhiteSpace(t) && !tags.Contains(t, StringComparer.OrdinalIgnoreCase))
            {
                tags.Add(t.Trim());
            }
        }

        return tags;
    }

}
