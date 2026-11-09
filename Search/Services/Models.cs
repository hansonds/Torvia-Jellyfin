using Plugin = PhoenicxTorBox.Plugin;
namespace TorviaSearch.Services;

/// <summary>One search result.</summary>
public sealed class TitleResult
{
    /// <summary>Gets or sets "movie" or "tv".</summary>
    public string Type { get; set; } = "movie";

    /// <summary>Gets or sets the TMDB id.</summary>
    public int TmdbId { get; set; }

    /// <summary>Gets or sets the title.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Gets or sets the year.</summary>
    public int? Year { get; set; }

    /// <summary>Gets or sets the overview.</summary>
    public string Overview { get; set; } = string.Empty;

    /// <summary>Gets or sets the poster URL.</summary>
    public string? Poster { get; set; }

    /// <summary>Gets or sets the backdrop URL.</summary>
    public string? Backdrop { get; set; }

    /// <summary>Gets or sets the TMDB rating.</summary>
    public double? Rating { get; set; }
}

/// <summary>A movie or show with its seasons.</summary>
public sealed class TitleDetails
{
    /// <summary>Gets or sets the basic info.</summary>
    public TitleResult Title { get; set; } = new();

    /// <summary>Gets or sets the IMDb id.</summary>
    public string? ImdbId { get; set; }

    /// <summary>Gets or sets the seasons (TV only).</summary>
    public List<SeasonInfo> Seasons { get; set; } = new();

    /// <summary>Gets or sets the Jellyfin item id when it is already in the library.</summary>
    public string? JellyfinItemId { get; set; }
}

/// <summary>One season.</summary>
public sealed class SeasonInfo
{
    /// <summary>Gets or sets the season number.</summary>
    public int Season { get; set; }

    /// <summary>Gets or sets the name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode count.</summary>
    public int Episodes { get; set; }

    /// <summary>Gets or sets the first air year.</summary>
    public int? Year { get; set; }
}

/// <summary>One cached release, worked out from its name.</summary>
public sealed class ReleaseInfo
{
    /// <summary>Gets or sets the info hash (lowercase).</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets the torrent name.</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Gets or sets the file name (for packs, the file inside).</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>Gets or sets the size in bytes (0 if unknown).</summary>
    public long SizeBytes { get; set; }

    /// <summary>Gets or sets the size as text, e.g. "2.4 GB".</summary>
    public string Size { get; set; } = string.Empty;

    /// <summary>Gets or sets the seeders.</summary>
    public int Seeders { get; set; }

    /// <summary>Gets or sets "Fast", "Good" or "Slow" from the seeders.</summary>
    public string Speed { get; set; } = string.Empty;

    /// <summary>Gets or sets the quality: 4K, 1080p, 720p, 480p or "?".</summary>
    public string Quality { get; set; } = "?";

    /// <summary>Gets or sets the source: WEB-DL, WEBRip, BluRay, Remux, HDTV, DVDRip, CAM/TS or "".</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a CAM/TS (poor) release.</summary>
    public bool IsCam { get; set; }

    /// <summary>Gets or sets the video codec.</summary>
    public string Codec { get; set; } = string.Empty;

    /// <summary>Gets or sets "DV", "HDR10+", "HDR10", "HDR" or "SDR".</summary>
    public string Hdr { get; set; } = "SDR";

    /// <summary>Gets or sets 8 or 10.</summary>
    public int BitDepth { get; set; } = 8;

    /// <summary>Gets or sets the audio formats, e.g. "DD+ 5.1 Atmos".</summary>
    public string Audio { get; set; } = string.Empty;

    /// <summary>Gets or sets the languages (short names).</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Gets or sets the subtitles note, e.g. "ESub".</summary>
    public string Subtitles { get; set; } = string.Empty;

    /// <summary>Gets or sets the file type, e.g. "MKV".</summary>
    public string Container { get; set; } = string.Empty;

    /// <summary>Gets or sets a value indicating whether this is a pack (more than one episode).</summary>
    public bool IsSeasonPack { get; set; }

    /// <summary>Gets or sets the TV kind: episode, range, season, multiseason, complete, pack. Empty for movies.</summary>
    public string Kind { get; set; } = string.Empty;

    /// <summary>Gets or sets a short TV label, e.g. "S01E05", "Season 1 pack", "Seasons 1–3".</summary>
    public string Label { get; set; } = string.Empty;

    /// <summary>Gets or sets the episode of the file that plays, e.g. "S01E05" (TV packs only).</summary>
    public string PlaysLabel { get; set; } = string.Empty;

    /// <summary>Gets or sets the whole torrent size in bytes as TorBox reports it (0 if unknown).</summary>
    public long PackSizeBytes { get; set; }

    /// <summary>Gets or sets the whole torrent size as text, set only when it differs from the file size.</summary>
    public string PackSize { get; set; } = string.Empty;

    /// <summary>Gets or sets a short one-line summary for lists.</summary>
    public string Summary { get; set; } = string.Empty;
}

/// <summary>What the client sends to add a release.</summary>
public sealed class AddRequest
{
    /// <summary>Gets or sets the info hash from the releases list.</summary>
    public string Hash { get; set; } = string.Empty;

    /// <summary>Gets or sets the tag (empty = default for the type).</summary>
    public string? Tag { get; set; }
}

/// <summary>Result of an add.</summary>
public sealed class AddResult
{
    /// <summary>Gets or sets a value indicating whether it worked.</summary>
    public bool Ok { get; set; }

    /// <summary>Gets or sets a message to show.</summary>
    public string Message { get; set; } = string.Empty;

    /// <summary>Gets or sets the TorBox torrent id.</summary>
    public long? TorrentId { get; set; }

    /// <summary>Gets or sets the name it was added with.</summary>
    public string? Name { get; set; }

    /// <summary>Gets or sets the tag set on it.</summary>
    public string? Tag { get; set; }

    /// <summary>Gets or sets a value indicating whether it was already in TorBox.</summary>
    public bool AlreadyInTorBox { get; set; }

    /// <summary>Gets or sets the direct TorBox stream link (if enabled and ready).</summary>
    public string? StreamUrl { get; set; }

    /// <summary>Gets or sets a value indicating whether a Torvia sync was started.</summary>
    public bool SyncStarted { get; set; }

    /// <summary>Gets or sets how many adds the user has left today (null = no limit).</summary>
    public int? RemainingToday { get; set; }

    /// <summary>Gets or sets the id of the saved record (for "My adds").</summary>
    public string? RecordId { get; set; }
}

/// <summary>What a release in the list remembers, so an add can only use a release the server found.</summary>
internal sealed class PendingRelease
{
    public ReleaseInfo Release { get; init; } = new();

    public string Type { get; init; } = "movie";

    public int TmdbId { get; init; }

    public string Title { get; init; } = string.Empty;

    public int? Year { get; init; }

    public string? Poster { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    public bool WholeSeason { get; init; }

    public DateTime Expires { get; init; }
}
