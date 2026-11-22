using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Jellyfin.Data;
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
        _batchTimer = new Timer(_ => _ = FlushAsync(), null, Timeout.Infinite, Timeout.Infinite);
        _dailyTimer = new Timer(_ => PruneSignedOut(), null, TimeSpan.FromMinutes(10), TimeSpan.FromDays(1));
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemAdded;
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

                // Send after 60 quiet seconds, but never more than 5 minutes after the first.
                var left = _firstPending + MaxWait - now;
                var wait = left < Quiet ? left : Quiet;
                _batchTimer?.Change(wait < TimeSpan.Zero ? TimeSpan.Zero : wait, Timeout.InfiniteTimeSpan);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia push: could not queue a new item");
        }
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
        lock (_lock)
        {
            ids = _pending.Keys.ToList();
            _pending.Clear();
        }

        if (ids.Count == 0)
        {
            return;
        }

        await _sending.WaitAsync().ConfigureAwait(false);
        try
        {
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

    /// <summary>The data message: type, user and a JSON list of items, under 3.5 KB.</summary>
    private static Dictionary<string, string> Message(string userId, List<BaseItem> items)
    {
        var list = items.Select(Describe).ToList();
        while (true)
        {
            var data = new Dictionary<string, string>
            {
                ["type"] = "new_titles",
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
