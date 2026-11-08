using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.Movies;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Library;

/// <summary>
/// Release groups write their site name into the video file's own title
/// ("PSArips.com | Heart.of.the.Beast...", "www.1TamilMV.style"), and Jellyfin sometimes shows that
/// instead of the real name. For TorBox movies, Torvia puts the clean title (the folder name it
/// wrote) back and locks it, so the file can't overwrite it again.
/// </summary>
public sealed class TitleGuard : IHostedService
{
    private static readonly Regex Junk = new(
        @"www\.|https?://|\|" +
        @"|\.(com|org|net|in|ws|xyz|style|me|to|co|io|cc|se|li|pw|mx|ms|info|biz|site|online|club|top|lol|fans|cat|day|uno|onl|nl|gs|vip|link|win|pro|art|boo|zip|mov|sbs|cfd)\b" +
        @"|\b(2160p|1080p|720p|480p|x264|x265|h\.?26[45]|hevc|web-?dl|web-?rip|blu-?ray|brrip|bdrip|hdrip|dvdrip|esubs?)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex YearSuffix = new(@"\s*\((\d{4})\)\s*$", RegexOptions.Compiled);

    private readonly ILibraryManager _library;
    private readonly ILogger<TitleGuard> _log;
    private readonly ConcurrentDictionary<Guid, byte> _busy = new();

    /// <summary>
    /// Initializes a new instance of the <see cref="TitleGuard"/> class.
    /// </summary>
    public TitleGuard(ILibraryManager library, ILogger<TitleGuard> log)
    {
        _library = library;
        _log = log;
        Instance = this;
    }

    /// <summary>Gets the running guard.</summary>
    public static TitleGuard? Instance { get; private set; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded += OnItemChanged;
        _library.ItemUpdated += OnItemChanged;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken)
    {
        _library.ItemAdded -= OnItemChanged;
        _library.ItemUpdated -= OnItemChanged;
        return Task.CompletedTask;
    }

    /// <summary>Checks the TorBox movies behind these .strm files (after a sync). Returns how many were fixed.</summary>
    public int Sweep(IEnumerable<string> strmPaths)
    {
        var fixedCount = 0;
        foreach (var path in strmPaths)
        {
            try
            {
                if (_library.FindByPath(path, false) is { } item && Fix(item))
                {
                    fixedCount++;
                }
            }
            catch (Exception ex)
            {
                _log.LogDebug(ex, "Torvia: title check failed for {Path}", path);
            }
        }

        return fixedCount;
    }

    private void OnItemChanged(object? sender, ItemChangeEventArgs e)
    {
        try
        {
            if (e.Item is { } item)
            {
                Fix(item);
            }
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Torvia: title check failed");
        }
    }

    /// <summary>Puts the clean title back on a TorBox movie whose name was replaced by release junk.</summary>
    private bool Fix(BaseItem item)
    {
        if (item is not Movie movie || Plugin.Instance is not { } plugin)
        {
            return false;
        }

        var path = movie.Path;
        if (string.IsNullOrEmpty(path)
            || !path.EndsWith(".strm", StringComparison.OrdinalIgnoreCase)
            || !path.StartsWith(plugin.LibraryRoot, StringComparison.Ordinal))
        {
            return false;
        }

        var name = movie.Name ?? string.Empty;
        if (name.Length > 0 && !Junk.IsMatch(name))
        {
            return false;
        }

        // The folder Torvia made is "Title (Year)".
        var folder = Path.GetFileName(Path.GetDirectoryName(path)) ?? string.Empty;
        var title = YearSuffix.Replace(folder, string.Empty).Trim();
        if (title.Length == 0 || string.Equals(title, name, StringComparison.Ordinal) || !_busy.TryAdd(movie.Id, 0))
        {
            return false;
        }

        try
        {
            movie.Name = title;
            if (!movie.LockedFields.Contains(MetadataField.Name))
            {
                movie.LockedFields = movie.LockedFields.Append(MetadataField.Name).ToArray();
            }

            movie.UpdateToRepositoryAsync(ItemUpdateType.MetadataEdit, CancellationToken.None).GetAwaiter().GetResult();
            _log.LogInformation("Torvia: title \"{Old}\" replaced with \"{New}\"", name, title);
            return true;
        }
        finally
        {
            _busy.TryRemove(movie.Id, out _);
        }
    }
}
