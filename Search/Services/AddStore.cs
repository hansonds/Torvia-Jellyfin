using System.Text.Json;
using Microsoft.Extensions.Logging;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>One title a user added through Search &amp; Add.</summary>
public sealed class AddRecord
{
    /// <summary>Gets or sets the record id.</summary>
    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    /// <summary>Gets or sets the user id (no dashes).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the user name at the time of the add.</summary>
    public string UserName { get; set; } = string.Empty;

    /// <summary>Gets or sets when it was added (UTC).</summary>
    public DateTime When { get; set; }

    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the poster URL.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the season (TV).</summary>
    public int? Season { get; set; }

    /// <summary>Gets or sets the episode (TV, null for a whole season).</summary>
    public int? Episode { get; set; }

    /// <summary>Gets or sets the TV label, e.g. "S02E05" or "Season 2 pack".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the quality summary of the release.</summary>
    public string Quality { get; set; } = string.Empty;

    /// <summary>Gets or sets the size text.</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>Gets or sets the TorBox tag.</summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>Gets or sets the name it was added to TorBox with.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the release name.</summary>
    public string Release { get; set; } = string.Empty;

    /// <summary>Gets or sets the info hash.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets the TorBox torrent id.</summary>
    public long TorrentId { get; set; }

    /// <summary>Gets or sets a value indicating whether it was already in TorBox before this add.</summary>
    public bool AlreadyInTorBox { get; set; }
}

/// <summary>Keeps every add in a small JSON file in Jellyfin's data folder, so it survives restarts.</summary>
public class AddStore
{
    private const int MaxRecords = 5000;
    private readonly object _lock = new();
    private readonly ILogger<AddStore> _log;
    private List<AddRecord>? _items;

    /// <summary>Initializes a new instance of the <see cref="AddStore"/> class.</summary>
    public AddStore(ILogger<AddStore> log) => _log = log;

    private static string FilePath => Path.Combine(Plugin.Instance!.DataPath, "torvia-search", "adds.json");

    /// <summary>All records, newest first.</summary>
    public List<AddRecord> All()
    {
        lock (_lock)
        {
            return Load().OrderByDescending(r => r.When).ToList();
        }
    }

    /// <summary>Adds a record.</summary>
    public void Add(AddRecord r)
    {
        lock (_lock)
        {
            var items = Load();
            items.Add(r);
            if (items.Count > MaxRecords)
            {
                items.RemoveRange(0, items.Count - MaxRecords);
            }

            Save(items);
        }
    }

    /// <summary>Gets a record by id.</summary>
    public AddRecord? Get(string id)
    {
        lock (_lock)
        {
            return Load().FirstOrDefault(r => r.Id == id);
        }
    }

    /// <summary>Removes a record.</summary>
    public void Remove(string id)
    {
        lock (_lock)
        {
            var items = Load();
            if (items.RemoveAll(r => r.Id == id) > 0)
            {
                Save(items);
            }
        }
    }

    /// <summary>How many new titles this user added today (UTC); "already in TorBox" adds don't count.</summary>
    public int CountToday(Guid userId)
    {
        var uid = userId.ToString("N");
        var today = DateTime.UtcNow.Date;
        lock (_lock)
        {
            return Load().Count(r => r.UserId == uid && !r.AlreadyInTorBox && r.When >= today);
        }
    }

    /// <summary>How many records (other than this one) point at the same TorBox item.</summary>
    public int OthersUsing(AddRecord r)
    {
        lock (_lock)
        {
            return Load().Count(x => x.Id != r.Id && (x.TorrentId == r.TorrentId || x.Hash.Equals(r.Hash, StringComparison.OrdinalIgnoreCase)));
        }
    }

    private List<AddRecord> Load()
    {
        if (_items != null)
        {
            return _items;
        }

        try
        {
            if (File.Exists(FilePath))
            {
                _items = JsonSerializer.Deserialize<List<AddRecord>>(File.ReadAllText(FilePath)) ?? new();
                return _items;
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning("Torvia Search: could not read {File}: {Msg}", FilePath, ex.Message);
        }

        _items = new();
        return _items;
    }

    private void Save(List<AddRecord> items)
    {
        _items = items;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(items));
            File.Move(tmp, FilePath, true);
        }
        catch (Exception ex)
        {
            _log.LogWarning("Torvia Search: could not save {File}: {Msg}", FilePath, ex.Message);
        }
    }
}
