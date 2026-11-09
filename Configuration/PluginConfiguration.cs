using MediaBrowser.Model.Plugins;

namespace PhoenicxTorBox.Configuration;

/// <summary>
/// Settings shown on the plugin page in the Jellyfin dashboard.
/// </summary>
public class PluginConfiguration : BasePluginConfiguration
{
    /// <summary>Gets or sets the TorBox API key (torbox.app → Settings → API).</summary>
    public string TorBoxApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the address apps use to reach this server (e.g. https://jellyfin.example.com).
    /// Empty = automatic: the address each app used to connect.
    /// </summary>
    public string PublicServerUrl { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the folder the .strm files are written to. Empty = the default folder inside
    /// Jellyfin's data folder (shown on the plugin page).
    /// </summary>
    public string LibraryPath { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets how often TorBox is synced, in minutes. 0 = only at startup and with "Sync now".
    /// </summary>
    public int SyncIntervalMinutes { get; set; } = 180;

    /// <summary>
    /// Gets or sets a value indicating whether each viewer is sent to the TorBox server closest to
    /// them (their address is passed to TorBox when the link is made).
    /// </summary>
    public bool NearestServerPerViewer { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether usenet downloads are included.</summary>
    public bool IncludeUsenet { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether web downloads are included.</summary>
    public bool IncludeWebDownloads { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether clients are told to play the TorBox link themselves
    /// (video goes TorBox → client). Off = Jellyfin decides as for any other .strm file.
    /// </summary>
    public bool DirectToClients { get; set; } = true;

    /// <summary>
    /// Gets or sets a value indicating whether tagged TorBox downloads go into a folder per tag
    /// (Tags/&lt;tag&gt;), so each tag can be its own library. Untagged ones stay in Movies / Shows.
    /// </summary>
    public bool FoldersByTag { get; set; } = true;

    /// <summary>
    /// Gets or sets name fixes, one per line: <c>text in the TorBox name = Title (Year)</c>.
    /// A file whose TorBox name or file name contains the text gets that title.
    /// </summary>
    public string NameFixes { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the secret used to sign play links, so nobody can guess a link to your files.
    /// Created on first start.
    /// </summary>
    public string SigningSecret { get; set; } = string.Empty;

    // ---------------- Torvia Search (second tab) ----------------

    /// <summary>Gets or sets the TMDB API key (v3 key or v4 read access token) for Torvia Search.</summary>
    public string TmdbApiKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Torrentio address (may include provider options).</summary>
    public string TorrentioUrl { get; set; } = "https://torrentio.strem.fun";

    /// <summary>Gets or sets a value indicating whether every signed-in user may use Search &amp; Add.</summary>
    public bool AllowAllUsers { get; set; }

    /// <summary>Gets or sets the ids of the users who may use Search &amp; Add (admins always may).</summary>
    public string[] AllowedUserIds { get; set; } = Array.Empty<string>();

    /// <summary>Gets or sets the tags users can pick when adding, comma separated.</summary>
    public string Tags { get; set; } = "movies,tv-shows";

    /// <summary>Gets or sets the default tag for movies added with Search &amp; Add.</summary>
    public string MovieTag { get; set; } = "movies";

    /// <summary>Gets or sets the default tag for TV shows added with Search &amp; Add.</summary>
    public string TvTag { get; set; } = "tv-shows";

    /// <summary>Gets or sets how many titles one non-admin user may add per day (0 = no limit).</summary>
    public int DailyLimitPerUser { get; set; } = 10;

    /// <summary>Gets or sets a value indicating whether CAM/TS releases are shown (always sorted last).</summary>
    public bool ShowCam { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether Torvia syncs right after an add.</summary>
    public bool SyncTorvia { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether the add result includes a direct TorBox stream link.</summary>
    public bool ReturnStreamLink { get; set; } = true;

    /// <summary>Gets or sets a value indicating whether old Torvia Search settings were already brought over.</summary>
    public bool SearchSettingsImported { get; set; }
}
