using Plugin = PhoenicxTorBox.Plugin;
namespace TorviaSearch.Services;

/// <summary>Filters and sorting for the releases list.</summary>
public sealed class ReleaseFilter
{
    /// <summary>Gets or sets the qualities to keep (4K, 1080p, 720p, 480p). Empty = all.</summary>
    public List<string> Qualities { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether only 10-bit releases are kept.</summary>
    public bool TenBit { get; set; }

    /// <summary>Gets or sets a value indicating whether only HDR / Dolby Vision releases are kept.</summary>
    public bool Hdr { get; set; }

    /// <summary>Gets or sets the languages; a release is kept if it has any of them. Empty = all.</summary>
    public List<string> Languages { get; set; } = new();

    /// <summary>Gets or sets a value indicating whether CAM/TS releases are removed.</summary>
    public bool HideCam { get; set; }

    /// <summary>Gets or sets the TV type: "episode" (single episode only) or "pack". Empty = both.</summary>
    public string? Kind { get; set; }

    /// <summary>Gets or sets the order: best (default), smallest, largest, seeders.</summary>
    public string? Sort { get; set; }

    /// <summary>Applies the filter and the order.</summary>
    public static List<ReleaseInfo> Apply(List<ReleaseInfo> list, ReleaseFilter f)
    {
        IEnumerable<ReleaseInfo> q = list;
        if (f.Qualities.Count > 0)
        {
            q = q.Where(r => f.Qualities.Contains(r.Quality, StringComparer.OrdinalIgnoreCase)
                || (r.Quality == "4K" && f.Qualities.Contains("2160p", StringComparer.OrdinalIgnoreCase)));
        }

        if (f.TenBit)
        {
            q = q.Where(r => r.BitDepth == 10);
        }

        if (f.Hdr)
        {
            q = q.Where(r => r.Hdr != "SDR");
        }

        if (f.Languages.Count > 0)
        {
            q = q.Where(r => r.Languages.Any(l => f.Languages.Contains(l, StringComparer.OrdinalIgnoreCase)));
        }

        if (string.Equals(f.Kind, "episode", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(r => !r.IsSeasonPack);
        }
        else if (string.Equals(f.Kind, "pack", StringComparison.OrdinalIgnoreCase))
        {
            q = q.Where(r => r.IsSeasonPack);
        }

        if (f.HideCam)
        {
            q = q.Where(r => !r.IsCam);
        }

        return (f.Sort ?? "best").ToLowerInvariant() switch
        {
            "smallest" => q.OrderBy(r => r.IsCam ? 1 : 0).ThenBy(r => r.SizeBytes <= 0 ? long.MaxValue : r.SizeBytes).ToList(),
            "largest" => q.OrderBy(r => r.IsCam ? 1 : 0).ThenByDescending(r => r.SizeBytes).ToList(),
            "seeders" => q.OrderBy(r => r.IsCam ? 1 : 0).ThenByDescending(r => r.Seeders).ToList(),
            _ => q.ToList(), // already in "Best" order
        };
    }

    /// <summary>What can be filtered on, with counts, so apps only show options that exist.</summary>
    public static object Facets(List<ReleaseInfo> list)
    {
        var order = new[] { "4K", "1080p", "720p", "480p", "?" };
        return new
        {
            qualities = list.GroupBy(r => r.Quality)
                .OrderBy(g => Array.IndexOf(order, g.Key))
                .Select(g => new { value = g.Key, count = g.Count() }),
            languages = list.SelectMany(r => r.Languages).GroupBy(l => l)
                .OrderByDescending(g => g.Count()).ThenBy(g => g.Key)
                .Select(g => new { value = g.Key, count = g.Count() }),
            tenBit = list.Count(r => r.BitDepth == 10),
            hdr = list.Count(r => r.Hdr != "SDR"),
            cam = list.Count(r => r.IsCam),
            episodes = list.Count(r => r.Kind.Length > 0 && !r.IsSeasonPack),
            packs = list.Count(r => r.IsSeasonPack),
        };
    }
}
