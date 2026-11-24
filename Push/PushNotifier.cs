using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
using Jellyfin.Data.Enums;
using Jellyfin.Data.Queries;
using Jellyfin.Database.Implementations.Enums;
using MediaBrowser.Controller.Devices;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Push;

/// <summary>
/// Watches for new movies, series and episodes in Jellyfin and sends each user one push to
/// PHOENIXA with what they can see. New items are collected until none has arrived for 60
/// seconds (at most 5 minutes), so a sync with ten episodes gives one notification.
/// </summary>
public sealed class PushNotifier : IHostedService, IDisposable
{
    private const int MaxBatch = 50;
    private const int MaxItems = 10;
    private const int MaxPayloadBytes = 3500;
    private static readonly TimeSpan Quiet = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(2);

    private readonly ILibraryManager _library;
    private readonly IUserManager _users;
    private readonly IDeviceManager _devices;
    private readonly PushStore _store;
    private readonly FcmSender _fcm;
    private readonly ILogger<PushNotifier> _log;
    private readonly ConcurrentDictionary<Guid, byte> _pending = new();

    // Removed movies and series, as they were when removed (the item itself is gone by the time
    // the batch is sent), with the users who could see them.
    private readonly ConcurrentDictionary<Guid, Removed> _removed = new();
    private readonly object _lock = new();
    private readonly SemaphoreSlim _sending = new(1, 1);
    private Timer? _batchTimer;
    private Timer? _dailyTimer;
    private DateTime _firstPending;

    /// <summary>Initializes a new instance of the <see cref="PushNotifier"/> class.</summary>
    public PushNotifier(ILibraryManager library, IUserManager users, IDeviceManager devices, PushStore store, FcmSender fcm, ILogger<PushNotifier> log)
    {
        _library = library;
        _users = users;
        _devices = devices;
        _store = store;
        _fcm = fcm;
        _log = log;
    }

    /// <summary>
    /// The Firebase settings, when push is switched on and fully set up; otherwise null.
    /// </summary>
    public (string ProjectId, string AppId, string ApiKey, string SenderId)? Firebase()
    {
        var c = Plugin.Instance?.Configuration;
        if (c is null || !c.PushEnabled || string.IsNullOrWhiteSpace(c.PushProjectId) || string.IsNullOrWhiteSpace(c.PushAppId)
            || string.IsNullOrWhiteSpace(c.PushApiKey) || string.IsNullOrWhiteSpace(c.PushSenderId) || _store.Account() is null)
        {
            return null;
        }

        return (c.PushProjectId, c.PushAppId, c.PushApiKey, c.PushSenderId);
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemAdded;
        _library.ItemRemoved += OnItemRemoved;
        _batchTimer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _dailyTimer = new Timer(_ => PruneSignedOut(), null, TimeSpan.FromMinutes(10), TimeSpan.FromDays(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
        _library.ItemRemoved -= OnItemRemoved;
        _batchTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        _dailyTimer?.Change(Timeout.Infinite, Timeout.Infinite);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _batchTimer?.Dispose();
        _dailyTimer?.Dispose();
        _sending.Dispose();
    }

    /// <summary>Sends a test message to all of this user's devices. Returns how many got it.</summary>
    public async Task<int> SendTestAsync(Guid userId, CancellationToken ct)
    {
        var fb = Firebase();
        if (fb is null)
        {
            return 0;
        }

        PruneSignedOut();
        var uid = userId.ToString("N");
        var devices = _store.All().Where(d => string.Equals(d.UserId, uid, StringComparison.OrdinalIgnoreCase)).ToList();
        var data = new Dictionary<string, string> { ["type"] = "test", ["userId"] = uid };
        var (ok, failed) = await SendAsync(fb.Value.ProjectId, devices, data, ct).ConfigureAwait(false);
        _log.LogInformation("Torvia push: test for {User}: {Ok} device(s) ok, {Failed} failed", Name(userId), ok, failed);
        return ok;
    }

    /// <summary>
    /// Sends a message written by the server owner to the chosen users (all users with a device
    /// when none are chosen). Disabled users are skipped. Returns users reached and devices ok/failed.
    /// </summary>
    public async Task<(int Users, int Ok, int Failed)> SendMessageAsync(string title, string body, IReadOnlyCollection<Guid>? only, CancellationToken ct)
    {
        var fb = Firebase();
        if (fb is null)
        {
            return (0, 0, 0);
        }

        PruneSignedOut();
        int users = 0, ok = 0, failed = 0;
        foreach (var group in _store.All().GroupBy(d => d.UserId, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var userId) || (only is { Count: > 0 } && !only.Contains(userId))
                || _users.GetUserById(userId) is not { } user || user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            var uid = userId.ToString("N");
            var data = new Dictionary<string, string> { ["type"] = "message", ["userId"] = uid, ["title"] = title, ["body"] = body };
            var (o, f) = await SendAsync(fb.Value.ProjectId, group.ToList(), data, ct).ConfigureAwait(false);
            if (o > 0)
            {
                users++;
            }

            ok += o;
            failed += f;
        }

        _log.LogInformation("Torvia push: message sent to {Users} user(s), {Ok} device(s) ok, {Failed} failed", users, ok, failed);
        return (users, ok, failed);
    }

    /// <summary>Removes devices that are no longer signed in to Jellyfin. Never fails.</summary>
    public void PruneSignedOut()
    {
        try
        {
            var gone = _store.All()
                .Select(d => d.DeviceId)
                .Distinct(StringComparer.Ordinal)
                .Where(id => _devices.GetDevices(new DeviceQuery { DeviceId = id }).Items.Count == 0)
                .ToList();
            var n = _store.RemoveDevices(gone);
            if (n > 0)
            {
                _log.LogInformation("Torvia push: removed {Count} device(s) that are no longer signed in", n);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Torvia push: could not check signed-in devices: {Msg}", ex.Message);
        }
    }

    private void OnItemAdded(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (Firebase() is null || !IsNew(e.Item))
            {
                return;
            }

            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_pending.IsEmpty)
                {
                    _firstPending = now;
                }

                _pending.TryAdd(e.Item.Id, 0);
                Schedule(now);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia push: could not queue a new item");
        }
    }

    // Send after 60 quiet seconds, but never more than 5 minutes after the first item. Call with _lock held.
    private void Schedule(DateTime now)
    {
        var left = _firstPending + MaxWait - now;
        var wait = left < Quiet ? left : Quiet;
        _batchTimer?.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
    }

    private sealed record Removed(Guid Id, string Type, string Name, int? Year, Dictionary<string, string> ProviderIds, HashSet<string> Users);

    private void OnItemRemoved(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            var item = e.Item;
            if (Plugin.Instance?.Configuration.PushRemovals != true || Firebase() is null || item is null
                || item.IsVirtualItem || item.ExtraType is not null || !item.OwnerId.Equals(Guid.Empty)
                || item is not (Movie or Series))
            {
                return;
            }

            // Who could see it, worked out now while the item is still complete.
            var users = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var uid in _store.All().Select(d => d.UserId).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    if (Guid.TryParse(uid, out var g) && _users.GetUserById(g) is { } user && item.IsVisibleStandalone(user))
                    {
                        users.Add(uid);
                    }
                }
                catch (Exception)
                {
                    // Can't tell for this user: leave them out.
                }
            }

            if (users.Count == 0)
            {
                return;
            }

            var removed = new Removed(item.Id, item is Series ? "Series" : "Movie", item.Name ?? string.Empty, item.ProductionYear,
                new Dictionary<string, string>(item.ProviderIds ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase), users);
            lock (_lock)
            {
                var now = DateTime.UtcNow;
                if (_pending.IsEmpty && _removed.IsEmpty)
                {
                    _firstPending = now;
                }

                _removed[item.Id] = removed;
                Schedule(now);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia push: could not queue a removed item");
        }
    }

    /// <summary>
    /// True when the title is still in Jellyfin after all: Torvia renamed or moved its file, a
    /// second copy exists, or it was added again. Those aren't real removals.
    /// </summary>
    private bool StillThere(Removed r)
    {
        var kind = r.Type == "Series" ? BaseItemKind.Series : BaseItemKind.Movie;
        foreach (var key in new[] { "Tmdb", "Imdb", "Tvdb" })
        {
            if (r.ProviderIds.TryGetValue(key, out var value) && !string.IsNullOrWhiteSpace(value))
            {
                var hits = _library.GetItemList(new InternalItemsQuery
                {
                    IncludeItemTypes = new[] { kind },
                    Recursive = true,
                    IsVirtualItem = false,
                    HasAnyProviderId = new Dictionary<string, string> { [key] = value },
                });
                if (hits.Any(h => !h.Id.Equals(r.Id)))
                {
                    return true;
                }
            }
        }

        if (string.IsNullOrWhiteSpace(r.Name))
        {
            return false;
        }

        var byName = _library.GetItemList(new InternalItemsQuery
        {
            IncludeItemTypes = new[] { kind },
            Recursive = true,
            IsVirtualItem = false,
            Name = r.Name,
        });
        return byName.Any(h => !h.Id.Equals(r.Id) && (r.Year is null || h.ProductionYear is null || h.ProductionYear == r.Year));
    }

    /// <summary>A real, new movie, series or episode (not a trailer, extra, missing episode or old file).</summary>
    private static bool IsNew(BaseItem? item)
    {
        if (item is null || item.IsVirtualItem || item.ExtraType is not null || !item.OwnerId.Equals(Guid.Empty))
        {
            return false;
        }

        var ok = item switch
        {
            Movie => !string.IsNullOrEmpty(item.Path),
            Episode ep => !ep.IsMissingEpisode && !string.IsNullOrEmpty(item.Path),
            Series => true,
            _ => false,
        };

        // Torvia rewrites links with the old file date, so those don't count as new.
        return ok && DateTime.UtcNow - item.DateCreated <= MaxAge;
    }

    private async Task FlushAsync()
    {
        List<Guid> ids;
        List<Removed> gone;
        lock (_lock)
        {
            ids = _pending.Keys.ToList();
            _pending.Clear();
            gone = _removed.Values.ToList();
            _removed.Clear();
        }

        if (ids.Count == 0 && gone.Count == 0)
        {
            return;
        }

        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
            await SendRemovedAsync(gone).ConfigureAwait(false);
            if (ids.Count == 0)
            {
                return;
            }

            if (ids.Count > MaxBatch)
            {
                _log.LogInformation("Torvia push: {Count} new items at once (a library scan?), no notification sent", ids.Count);
                return;
            }

            var fb = Firebase();
            if (fb is null)
            {
                return;
            }

            // Read the items again: by now Jellyfin has usually fetched their real names.
            var items = ids.Select(id => _library.GetItemById(id)).Where(IsNew).Select(i => i!).ToList();
            if (items.Count == 0)
            {
                return;
            }

            PruneSignedOut();
            foreach (var group in _store.All().GroupBy(d => d.UserId, StringComparer.OrdinalIgnoreCase))
            {
                if (!Guid.TryParse(group.Key, out var userId) || _users.GetUserById(userId) is not { } user
                    || user.HasPermission(PermissionKind.IsDisabled))
                {
                    continue;
                }

                var visible = items.Where(i =>
                {
                    try
                    {
                        return i.IsVisibleStandalone(user);
                    }
                    catch (Exception ex)
                    {
                        _log.LogDebug(ex, "Torvia push: could not check access to {Name}", i.Name);
                        return false;
                    }
                }).ToList();
                var seriesIds = visible.OfType<Series>().Select(s => s.Id).ToHashSet();
                visible.RemoveAll(i => i is Episode ep && seriesIds.Contains(ep.SeriesId));
                if (visible.Count == 0)
                {
                    continue;
                }

                var uid = userId.ToString("N");
                var data = Message(uid, visible.OrderByDescending(i => i.DateCreated).Take(MaxItems).ToList());
                var (ok, failed) = await SendAsync(fb.Value.ProjectId, group.ToList(), data, CancellationToken.None).ConfigureAwait(false);
                _log.LogInformation("Torvia push: {User}: {Count} item(s), {Ok} device(s) ok, {Failed} failed", user.Username, visible.Count, ok, failed);
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Torvia push: could not send notifications");
        }
        finally
        {
            _sending.Release();
        }
    }

    /// <summary>
    /// One "removed" notification per user, for real removals only (not renames, moves or second
    /// copies). A big clean-up (more than 50 at once) sends nothing.
    /// </summary>
    private async Task SendRemovedAsync(List<Removed> gone)
    {
        if (gone.Count == 0)
        {
            return;
        }

        if (gone.Count > MaxBatch)
        {
            _log.LogInformation("Torvia push: {Count} titles removed at once, no notification sent", gone.Count);
            return;
        }

        var fb = Firebase();
        if (fb is null || Plugin.Instance?.Configuration.PushRemovals != true)
        {
            return;
        }

        var real = gone.Where(r => !StillThere(r)).ToList();
        if (real.Count == 0)
        {
            return;
        }

        foreach (var group in _store.All().GroupBy(d => d.UserId, StringComparer.OrdinalIgnoreCase))
        {
            if (!Guid.TryParse(group.Key, out var userId) || _users.GetUserById(userId) is not { } user
                || user.HasPermission(PermissionKind.IsDisabled))
            {
                continue;
            }

            var mine = real.Where(r => r.Users.Contains(group.Key)).Take(MaxItems).Select(r =>
            {
                var d = new Dictionary<string, object> { ["id"] = r.Id.ToString("N"), ["type"] = r.Type, ["name"] = Cut(r.Name, 120) };
                if (r.Year is { } y)
                {
                    d["year"] = y;
                }

                return d;
            }).ToList();
            if (mine.Count == 0)
            {
                continue;
            }

            var data = Message(group.Key.ToLowerInvariant(), mine, "removed_titles");
            var (ok, failed) = await SendAsync(fb.Value.ProjectId, group.ToList(), data, CancellationToken.None).ConfigureAwait(false);
            _log.LogInformation("Torvia push: {User}: {Count} removed, {Ok} device(s) ok, {Failed} failed", user.Username, mine.Count, ok, failed);
        }
    }

    /// <summary>The data message: type, user and a JSON list of items, under 3.5 KB.</summary>
    private static Dictionary<string, string> Message(string userId, List<BaseItem> items)
        => Message(userId, items.Select(Describe).ToList(), "new_titles");

    private static Dictionary<string, string> Message(string userId, List<Dictionary<string, object>> list, string type)
    {
        while (true)
        {
            var data = new Dictionary<string, string>
            {
                ["type"] = type,
                ["userId"] = userId,
                ["items"] = JsonSerializer.Serialize(list),
            };
            var size = data.Sum(kv => Encoding.UTF8.GetByteCount(kv.Key) + Encoding.UTF8.GetByteCount(kv.Value));
            if (size <= MaxPayloadBytes || list.Count <= 1)
            {
                return data;
            }

            list.RemoveAt(list.Count - 1);
        }
    }

    private static Dictionary<string, object> Describe(BaseItem i)
    {
        var d = new Dictionary<string, object>
        {
            ["id"] = i.Id.ToString("N"),
            ["type"] = i switch { Episode => "Episode", Series => "Series", _ => "Movie" },
            ["name"] = Cut(i.Name ?? string.Empty, 120),
        };
        if (i.ProductionYear is { } y)
        {
            d["year"] = y;
        }

        if (i is Episode ep)
        {
            if (!ep.SeriesId.Equals(Guid.Empty))
            {
                d["seriesId"] = ep.SeriesId.ToString("N");
            }

            if (!string.IsNullOrEmpty(ep.SeriesName))
            {
                d["seriesName"] = Cut(ep.SeriesName, 120);
            }

            if (ep.ParentIndexNumber is { } s)
            {
                d["season"] = s;
            }

            if (ep.IndexNumber is { } n)
            {
                d["episode"] = n;
            }
        }

        return d;
    }

    private static string Cut(string s, int max) => s.Length <= max ? s : s[..max];

    private async Task<(int Ok, int Failed)> SendAsync(string projectId, List<PushDevice> devices, Dictionary<string, string> data, CancellationToken ct)
    {
        int ok = 0, failed = 0;
        var bad = new List<string>();
        foreach (var d in devices)
        {
            switch (await _fcm.SendAsync(projectId, d.Token, data, ct).ConfigureAwait(false))
            {
                case SendResult.Ok:
                    ok++;
                    break;
                case SendResult.BadToken:
                    bad.Add(d.Token);
                    failed++;
                    break;
                default:
                    failed++;
                    break;
            }
        }

        var removed = _store.RemoveTokens(bad);
        if (removed > 0)
        {
            _log.LogInformation("Torvia push: removed {Count} device(s) Firebase no longer knows", removed);
        }

        return (ok, failed);
    }

    private string Name(Guid id) => _users.GetUserById(id)?.Username ?? id.ToString("N");
}
