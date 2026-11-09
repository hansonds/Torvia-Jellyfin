using System.Globalization;
using System.Text.RegularExpressions;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>What a TV release name says it contains.</summary>
public sealed class TvInfo
{
    /// <summary>Gets or sets the kind: episode, range, season, multiseason, complete or unknown.</summary>
    public string Kind { get; set; } = "unknown";

    /// <summary>Gets or sets the first season (null if not in the name).</summary>
    public int? SeasonFrom { get; set; }

    /// <summary>Gets or sets the last season.</summary>
    public int? SeasonTo { get; set; }

    /// <summary>Gets or sets the first episode.</summary>
    public int? EpisodeFrom { get; set; }

    /// <summary>Gets or sets the last episode.</summary>
    public int? EpisodeTo { get; set; }

    /// <summary>Gets a value indicating whether a season was found in the name.</summary>
    public bool HasSeason => SeasonFrom != null;

    /// <summary>True when the name covers this season.</summary>
    public bool CoversSeason(int s) => SeasonFrom == null || (s >= SeasonFrom && s <= (SeasonTo ?? SeasonFrom));

    /// <summary>True when the name covers this episode (only meaningful for episode / range).</summary>
    public bool CoversEpisode(int e) => EpisodeFrom == null || (e >= EpisodeFrom && e <= (EpisodeTo ?? EpisodeFrom));
}

/// <summary>Reads season / episode information from TV release names, including Indian-style names.</summary>
public static class TvParser
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
    private const string Sep = @"[\s._\-]*";

    // S01E05, S01 E05, S01.EP05, S01 EP 05, S1 - E5, S01 Ep.05, S01 Episode 5, with ranges like S01E01-E08, S01 EP(01-08), S01E01-08, S01 EP01 to EP08
    private static readonly Regex SeasonEpisode = new(
        @"(?<![a-z0-9])S(?:eason)?" + Sep + @"(\d{1,2})" + Sep + @"(?:E|EP|Ep\.|Episodes?)" + Sep + @"\(?\s*(\d{1,3})" +
        @"(?:\s*(?:-|–|~|to|&|\+)\s*(?:S\d{1,2}\s*)?(?:E|EP|Episodes?)?\s*(\d{1,3}))?\s*\)?(?![0-9])",
        I);

    // 1x05, 1x05-1x08
    private static readonly Regex XStyle = new(@"(?<![0-9a-z])(\d{1,2})x(\d{2,3})(?:\s*-\s*(?:\d{1,2}x)?(\d{2,3}))?(?![0-9])", I);

    // S01-S03, S01-03, Season 1-3, Seasons 1 to 4, S01 S02 S03
    private static readonly Regex MultiSeason = new(
        @"(?<![a-z0-9])(?:S|Seasons?" + Sep + @")(\d{1,2})" + @"\s*(?:-|–|~|to|&)\s*(?:S|Season\s*)?(\d{1,2})(?![0-9]|" + Sep + @"(?:E|EP)\d)",
        I);

    // S01, Season 1, Season.01, S01 Complete
    private static readonly Regex SeasonOnly = new(@"(?<![a-z0-9])(?:S|Season" + Sep + @")(\d{1,2})(?![0-9])(?!" + Sep + @"(?:E|EP|Ep\.|Episode)" + Sep + @"\(?\d)", I);

    // Episode 5, EP05, E05 (no season in the name)
    private static readonly Regex EpisodeOnly = new(@"(?<![a-z0-9])(?:EP|Episode|E)" + Sep + @"(\d{1,3})(?:\s*(?:-|–|~|to)\s*(?:EP|E)?\s*(\d{1,3}))?(?![0-9])", I);

    private static readonly Regex Complete = new(@"(?<![a-z])(complete|all[\s._-]?seasons|full[\s._-]?series|the[\s._-]?complete)(?![a-z])", I);

    /// <summary>Reads a release or file name.</summary>
    public static TvInfo Parse(string? name)
    {
        var t = new TvInfo();
        if (string.IsNullOrWhiteSpace(name))
        {
            return t;
        }

        // Years and resolutions can look like episode numbers; take them out first.
        var n = Regex.Replace(name, @"(?<![0-9])(19|20)\d{2}(?![0-9])|\d{3,4}p|x26[45]|h\.?26[45]|\d+(\.\d+)?\s*(gb|mb)|(?<![0-9])[257]\.[01](?![0-9])|10bit|8bit", " ", I);

        var multi = MultiSeason.Match(n);
        var se = SeasonEpisode.Match(n);
        if (multi.Success && (!se.Success || multi.Index < se.Index))
        {
            var a = Int(multi.Groups[1].Value);
            var b = Int(multi.Groups[2].Value);
            if (b > a && b - a < 40)
            {
                t.Kind = "multiseason";
                t.SeasonFrom = a;
                t.SeasonTo = b;
                return t;
            }
        }

        if (se.Success)
        {
            t.SeasonFrom = t.SeasonTo = Int(se.Groups[1].Value);
            t.EpisodeFrom = Int(se.Groups[2].Value);
            t.EpisodeTo = se.Groups[3].Success ? Int(se.Groups[3].Value) : t.EpisodeFrom;
            t.Kind = t.EpisodeTo > t.EpisodeFrom ? "range" : "episode";
            if (t.EpisodeTo < t.EpisodeFrom)
            {
                t.EpisodeTo = t.EpisodeFrom;
                t.Kind = "episode";
            }

            return t;
        }

        var x = XStyle.Match(n);
        if (x.Success)
        {
            t.SeasonFrom = t.SeasonTo = Int(x.Groups[1].Value);
            t.EpisodeFrom = Int(x.Groups[2].Value);
            t.EpisodeTo = x.Groups[3].Success ? Int(x.Groups[3].Value) : t.EpisodeFrom;
            t.Kind = t.EpisodeTo > t.EpisodeFrom ? "range" : "episode";
            return t;
        }

        var s = SeasonOnly.Match(n);
        if (s.Success)
        {
            t.SeasonFrom = t.SeasonTo = Int(s.Groups[1].Value);

            // "S01 E01-E08" was caught above; here a season may still be followed by a bare episode, e.g. "S01 - 05"
            var bare = Regex.Match(n[(s.Index + s.Length)..], @"^\s*[-–]\s*(\d{1,3})(?![0-9p])");
            if (bare.Success)
            {
                t.EpisodeFrom = t.EpisodeTo = Int(bare.Groups[1].Value);
                t.Kind = "episode";
                return t;
            }

            t.Kind = "season";
            return t;
        }

        var e = EpisodeOnly.Match(n);
        if (e.Success)
        {
            t.EpisodeFrom = Int(e.Groups[1].Value);
            t.EpisodeTo = e.Groups[2].Success ? Int(e.Groups[2].Value) : t.EpisodeFrom;
            t.Kind = t.EpisodeTo > t.EpisodeFrom ? "range" : "episode";
            return t;
        }

        if (Complete.IsMatch(n))
        {
            t.Kind = "complete";
        }

        return t;
    }

    /// <summary>A short label for the list, e.g. "S01E05", "Season 1 pack", "Seasons 1–3".</summary>
    public static string Label(TvInfo t) => t.Kind switch
    {
        "episode" => t.SeasonFrom != null ? $"S{t.SeasonFrom:00}E{t.EpisodeFrom:00}" : $"Episode {t.EpisodeFrom}",
        "range" => (t.SeasonFrom != null ? $"S{t.SeasonFrom:00} " : string.Empty) + $"Episodes {t.EpisodeFrom}–{t.EpisodeTo}",
        "season" => $"Season {t.SeasonFrom} pack",
        "multiseason" => $"Seasons {t.SeasonFrom}–{t.SeasonTo}",
        "complete" => "Complete series",
        _ => "Pack",
    };

    private static int Int(string s) => int.Parse(s, NumberStyles.None, CultureInfo.InvariantCulture);
}
