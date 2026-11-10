using System.Collections.Concurrent;
using System.Text.Json;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.Session;
using MediaBrowser.Model.Session;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Stats;

/// <summary>Saved totals for the "bandwidth saved" counter.</summary>
public sealed class StreamTotals
{
    /// <summary>Gets or sets the estimated bytes streamed straight from TorBox, all time.</summary>
    public long TotalBytes { get; set; }

    /// <summary>Gets or sets the watch time from TorBox, all time, in seconds.</summary>
    public long TotalSeconds { get; set; }

    /// <summary>Gets or sets the number of plays.</summary>
    public long Plays { get; set; }

    /// <summary>Gets or sets the current month ("2026-11").</summary>
    public string Month { get; set; } = string.Empty;

    /// <summary>Gets or sets the estimated bytes this month.</summary>
    public long MonthBytes { get; set; }

    /// <summary>Gets or sets the plays this month.</summary>
    public long MonthPlays { get; set; }

    /// <summary>Gets or sets the watch time this month, in seconds.</summary>
    public long MonthSeconds { get; set; }

    /// <summary>Gets or sets when counting started (UTC).</summary>
    public DateTime? SinceUtc { get; set; }
}

/// <summary>
/// Counts how much video was streamed straight from TorBox instead of through this server.
/// The server never sees that traffic, so it is estimated: time actually watched × the file's
/// bitrate, for TorBox items played directly.
/// </summary>
public sealed class StreamStats : IHostedService
{
    private readonly ISessionManager _sessions;
    private readonly IMediaSourceManager _mediaSources;
    private readonly ILogger<StreamStats> _log;
    private readonly ConcurrentDictionary<string, Tracker> _active = new();
    private readonly object _lock = new();
    private StreamTotals? _totals;
    private DateTime _lastSave = DateTime.MinValue;

    /// <summary>
    /// Initializes a new instance of the <see cref="StreamStats"/> class.
    /// </summary>
    public StreamStats(ISessionManager sessions, IMediaSourceManager mediaSources, ILogger<StreamStats> log)
    {
        _sessions = sessions;
        _mediaSources = mediaSources;
        _log = log;
        Instance = this;
    }

    /// <summary>Gets the running counter (for the plugin page).</summary>
    public static StreamStats? Instance { get; private set; }

    private static string FilePath => Path.Combine(Plugin.Instance!.LibraryRoot, ".torvia-stats.json");

    /// <summary>Current totals.</summary>
    public StreamTotals Totals
    {
        get
        {
            lock (_lock)
            {
                var t = Load();
                RollMonth(t);
                return new StreamTotals
                {
                    TotalBytes = t.TotalBytes,
                    TotalSeconds = t.TotalSeconds,
                    Plays = t.Plays,
                    Month = t.Month,
                    MonthBytes = t.MonthBytes,
                    MonthPlays = t.MonthPlays,
                    MonthSeconds = t.MonthSeconds,
                    SinceUtc = t.SinceUtc,
                };
            }
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart += OnStart;
        _sessions.PlaybackProgress += OnProgress;
        _sessions.PlaybackStopped += OnStop;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _sessions.PlaybackStart -= OnStart;
        _sessions.PlaybackProgress -= OnProgress;
        _sessions.PlaybackStopped -= OnStop;
        lock (_lock)
        {
            Save(force: true);
        }

        return Task.CompletedTask;
    }

    private void OnStart(object? sender, PlaybackProgressEventArgs e)
    {
        try
        {
            if (Key(e) is not { } key || !IsTorBoxDirect(e))
            {
                return;
            }

            var t = new Tracker { Position = e.PlaybackPositionTicks ?? 0, Seen = DateTime.UtcNow, BytesPerSecond = BytesPerSecond(e) };
            _active[key] = t;
            lock (_lock)
            {
                var s = Load();
                RollMonth(s);
                s.Plays++;
                s.MonthPlays++;
                Save(force: false);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia: stats start failed");
        }
    }

    private void OnProgress(object? sender, PlaybackProgressEventArgs e) => Count(e, false);

    private void OnStop(object? sender, PlaybackStopEventArgs e) => Count(e, true);

    private void Count(PlaybackProgressEventArgs e, bool stopped)
    {
        try
        {
            if (Key(e) is not { } key || !_active.TryGetValue(key, out var t))
            {
                return;
            }

            var now = DateTime.UtcNow;
            var pos = e.PlaybackPositionTicks ?? t.Position;
            var watched = TimeSpan.FromTicks(Math.Max(0, pos - t.Position)).TotalSeconds;

            // A jump (seek) can't count more than the real time that passed, plus some slack.
            var wall = (now - t.Seen).TotalSeconds * 1.5 + 5;
            var seconds = t.Paused ? 0 : Math.Min(watched, wall);

            t.Position = pos;
            t.Seen = now;
            t.Paused = e.IsPaused;

            if (seconds > 0)
            {
                lock (_lock)
                {
                    var s = Load();
                    RollMonth(s);
                    var bytes = (long)(seconds * t.BytesPerSecond);
                    s.TotalBytes += bytes;
                    s.MonthBytes += bytes;
                    s.TotalSeconds += (long)seconds;
                    s.MonthSeconds += (long)seconds;
                    Save(force: stopped);
                }
            }

            if (stopped)
            {
                _active.TryRemove(key, out _);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia: stats update failed");
        }
    }

    private static string? Key(PlaybackProgressEventArgs e) =>
        !string.IsNullOrEmpty(e.PlaySessionId) ? e.PlaySessionId
        : e.Session?.Id is { } sid && e.Item is not null ? sid + ":" + e.Item.Id : null;

    /// <summary>A TorBox item (its file is one of Torvia's .strm files) played directly.</summary>
    private static bool IsTorBoxDirect(PlaybackProgressEventArgs e)
    {
        var path = e.Item?.Path;
        if (string.IsNullOrEmpty(path) || Plugin.Instance is not { } p
            || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(p.LibraryRoot, StringComparison.Ordinal))
        {
            return false;
        }

        return e.Session?.PlayState?.PlayMethod is null or PlayMethod.DirectPlay;
    }

    private double BytesPerSecond(PlaybackProgressEventArgs e)
    {
        var sources = _mediaSources.GetStaticMediaSources(e.Item, false);
        var src = sources.FirstOrDefault(s => string.Equals(s.Id, e.MediaSourceId, StringComparison.OrdinalIgnoreCase)) ?? sources.FirstOrDefault();
        long bitrate = src?.Bitrate ?? 0;
        if (bitrate <= 0 && src?.MediaStreams is { } streams)
        {
            bitrate = streams.Where(m => m.BitRate.HasValue).Sum(m => (long)m.BitRate!.Value);
        }

        // Unknown bitrate: assume a typical 1080p file (8 Mbit/s).
        return (bitrate > 0 ? bitrate : 8_000_000) / 8.0;
    }

    private static void RollMonth(StreamTotals s)
    {
        // A new month (in the time zone set on the Torvia tab) starts again from zero.
        var month = PhoenicxTorBox.Plugin.LocalNow().ToString("yyyy-MM", System.Globalization.CultureInfo.InvariantCulture);
        if (s.Month != month)
        {
            s.Month = month;
            s.MonthBytes = 0;
            s.MonthPlays = 0;
            s.MonthSeconds = 0;
        }
    }

    private StreamTotals Load()
    {
        if (_totals is not null)
        {
            return _totals;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                _totals = JsonSerializer.Deserialize<StreamTotals>(File.ReadAllText(FilePath));
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia: stats file unreadable, starting new");
        }

        _totals ??= new StreamTotals { SinceUtc = DateTime.UtcNow };
        _totals.SinceUtc ??= DateTime.UtcNow;
        return _totals;
    }

    private void Save(bool force)
    {
        if (_totals is null || (!force && DateTime.UtcNow - _lastSave < TimeSpan.FromMinutes(1)))
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(Plugin.Instance!.LibraryRoot);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(_totals));
            _lastSave = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia: stats not saved");
        }
    }

    private sealed class Tracker
    {
        public long Position { get; set; }

        public DateTime Seen { get; set; }

        public bool Paused { get; set; }

        public double BytesPerSecond { get; set; }
    }
}
