using System.Security.Cryptography;
using System.Xml.Linq;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Model.Plugins;
using MediaBrowser.Model.Serialization;
using PhoenicxTorBox.Configuration;

namespace PhoenicxTorBox;

/// <summary>
/// Torvia: TorBox movies and series as Jellyfin libraries, streamed straight from TorBox.
/// Made by Hanson — https://phoenicx.org
/// </summary>
public class Plugin : BasePlugin<PluginConfiguration>, IHasWebPages
{
    /// <summary>Plugin id, also in build.yaml.</summary>
    public static readonly Guid PluginId = Guid.Parse("d793e3f9-364f-4640-9ebb-7c807bfcb90e");

    /// <summary>
    /// Initializes a new instance of the <see cref="Plugin"/> class.
    /// </summary>
    public Plugin(IApplicationPaths applicationPaths, IXmlSerializer xmlSerializer)
        : base(applicationPaths, xmlSerializer)
    {
        Instance = this;
        DataPath = applicationPaths.DataPath;

        if (string.IsNullOrWhiteSpace(Configuration.SigningSecret))
        {
            Configuration.SigningSecret = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            SaveConfiguration();
        }

        ImportOldSearchSettings(applicationPaths.PluginConfigurationsPath);
    }

    /// <summary>Gets the running plugin.</summary>
    public static Plugin? Instance { get; private set; }

    /// <summary>Gets Jellyfin's data folder.</summary>
    public string DataPath { get; }

    /// <inheritdoc />
    public override string Name => "Torvia";

    /// <inheritdoc />
    public override Guid Id => PluginId;

    /// <inheritdoc />
    public override string Description =>
        "Torvia — straight from TorBox to Jellyfin. Your TorBox movies and series as Jellyfin libraries, played straight from TorBox. By Hanson — phoenicx.org";

    /// <summary>Gets the folder the .strm files go to.</summary>
    public string LibraryRoot =>
        string.IsNullOrWhiteSpace(Configuration.LibraryPath)
            ? Path.Combine(DataPath, "phoenicx-torbox")
            : Configuration.LibraryPath.Trim();

    /// <summary>Gets the movies folder to add as a Movies library.</summary>
    public string MoviesPath => Path.Combine(LibraryRoot, "Movies");

    /// <summary>Gets the shows folder to add as a Shows library.</summary>
    public string ShowsPath => Path.Combine(LibraryRoot, "Shows");

    /// <summary>Gets the folder holding one folder per TorBox tag.</summary>
    public string TagsPath => Path.Combine(LibraryRoot, "Tags");

    /// <inheritdoc />
    public IEnumerable<PluginPageInfo> GetPages()
    {
        yield return new PluginPageInfo
        {
            Name = "Torvia",
            EnableInMainMenu = true,
            MenuIcon = "cloud",
            EmbeddedResourcePath = GetType().Namespace + ".Configuration.configPage.html",
        };
    }

    /// <summary>
    /// Torvia Search used to be its own plugin. Its settings (TMDB key, allowed users, tags, …) are
    /// brought over once, so nothing has to be typed again.
    /// </summary>
    private void ImportOldSearchSettings(string configFolder)
    {
        if (Configuration.SearchSettingsImported)
        {
            return;
        }

        try
        {
            var file = Path.Combine(configFolder, "TorviaSearch.xml");
            if (File.Exists(file) && XDocument.Load(file).Root is { } r)
            {
                string? S(string n) => r.Element(n)?.Value;
                bool? B(string n) => bool.TryParse(S(n), out var b) ? b : null;
                var c = Configuration;
                if (string.IsNullOrWhiteSpace(c.TmdbApiKey) && !string.IsNullOrWhiteSpace(S("TmdbApiKey")))
                {
                    c.TmdbApiKey = S("TmdbApiKey")!.Trim();
                }

                if (string.IsNullOrWhiteSpace(c.TorBoxApiKey) && !string.IsNullOrWhiteSpace(S("TorBoxApiKey")))
                {
                    c.TorBoxApiKey = S("TorBoxApiKey")!.Trim();
                }

                c.TorrentioUrl = S("TorrentioUrl") is { Length: > 0 } t ? t : c.TorrentioUrl;
                c.Tags = S("Tags") is { Length: > 0 } tags ? tags : c.Tags;
                c.MovieTag = S("MovieTag") is { Length: > 0 } mt ? mt : c.MovieTag;
                c.TvTag = S("TvTag") is { Length: > 0 } tt ? tt : c.TvTag;
                c.DailyLimitPerUser = int.TryParse(S("DailyLimitPerUser"), out var lim) ? lim : c.DailyLimitPerUser;
                c.AllowAllUsers = B("AllowAllUsers") ?? c.AllowAllUsers;
                c.ShowCam = B("ShowCam") ?? c.ShowCam;
                c.ReturnStreamLink = B("ReturnStreamLink") ?? c.ReturnStreamLink;
                var users = r.Element("AllowedUserIds")?.Elements().Select(e => e.Value.Trim()).Where(v => v.Length > 0).ToArray();
                if (users is { Length: > 0 })
                {
                    c.AllowedUserIds = users;
                }
            }

            Configuration.SearchSettingsImported = true;
            SaveConfiguration();
        }
        catch (Exception)
        {
            // Old settings unreadable: start with the defaults.
        }
    }
}
