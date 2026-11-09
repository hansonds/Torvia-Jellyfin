using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>Reads a release name and works out quality, source, codec, HDR, audio, languages and more.</summary>
public static class ReleaseParser
{
    private const RegexOptions I = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;

    // Word edges for scene names: dots, dashes, spaces, brackets, underscores.
    private const string L = @"(?<![a-z0-9])";
    private const string R = @"(?![a-z0-9])";

    private static readonly (string Label, Regex Rx)[] LangWords =
    {
        ("Tam", new Regex(L + "(tam|tamil)" + R, I)),
        ("Mal", new Regex(L + "(mal|malayalam)" + R, I)),
        ("Tel", new Regex(L + "(tel|telugu)" + R, I)),
        ("Hin", new Regex(L + "(hin|hindi)" + R, I)),
        ("Kan", new Regex(L + "(kan|kannada)" + R, I)),
        ("Ben", new Regex(L + "bengali" + R, I)),
        ("Mar", new Regex(L + "marathi" + R, I)),
        ("Eng", new Regex(L + "(eng|english)" + R, I)),
        ("Kor", new Regex(L + "(kor|korean)" + R, I)),
        ("Jpn", new Regex(L + "(jap|jpn|japanese)" + R, I)),
        ("Multi", new Regex(L + "(multi|multi-audio|dual[ .-]?audio)" + R, I)),
    };

    private static readonly Dictionary<string, string> FlagLang = new(StringComparer.Ordinal)
    {
        ["GB"] = "Eng", ["US"] = "Eng", ["IN"] = "Ind", ["JP"] = "Jpn", ["KR"] = "Kor", ["CN"] = "Chi", ["TW"] = "Chi",
        ["ES"] = "Spa", ["MX"] = "Spa", ["FR"] = "Fre", ["DE"] = "Ger", ["IT"] = "Ita", ["RU"] = "Rus", ["PT"] = "Por",
        ["BR"] = "Por", ["NL"] = "Dut", ["PL"] = "Pol", ["TR"] = "Tur", ["SA"] = "Ara", ["TH"] = "Tha", ["UA"] = "Ukr",
        ["VN"] = "Vie", ["ID"] = "Ind.", ["SE"] = "Swe", ["DK"] = "Dan", ["NO"] = "Nor", ["FI"] = "Fin", ["GR"] = "Gre",
        ["HU"] = "Hun", ["CZ"] = "Cze", ["RO"] = "Rum", ["IL"] = "Heb", ["IR"] = "Per", ["MY"] = "May", ["PH"] = "Fil",
    };

    /// <summary>Fills in everything that can be read from the names. Also takes Torrentio's flag line.</summary>
    public static void Parse(ReleaseInfo r, string? flagLine)
    {
        // File extensions are dropped so ".ts" is not read as a TeleSync.
        var text = Regex.Replace(r.Name + " " + r.FileName, @"\.(mkv|mp4|avi|m4v|ts|m2ts|webm|mov|wmv)(?=\s|$)", " ", I);

        // Quality
        r.Quality =
            Regex.IsMatch(text, L + "(2160p|4k|uhd)" + R, I) ? "4K" :
            Regex.IsMatch(text, L + "1080[pi]" + R, I) ? "1080p" :
            Regex.IsMatch(text, L + "720p" + R, I) ? "720p" :
            Regex.IsMatch(text, L + "(480p|576p|360p|sd)" + R, I) ? "480p" : "?";

        // Source (CAM/TS first, so a CAM tagged "HDRip" still counts as CAM)
        if (Regex.IsMatch(text, L + "(cam|camrip|hdcam|hq-?cam|ts|hdts|hd-?ts|telesync|tc|telecine|pre-?dvd|hdtc|s-?print)" + R, I))
        {
            r.Source = "CAM/TS";
            r.IsCam = true;
        }
        else if (Regex.IsMatch(text, "remux", I))
        {
            r.Source = "Remux";
        }
        else if (Regex.IsMatch(text, L + "web-?dl" + R, I))
        {
            r.Source = "WEB-DL";
        }
        else if (Regex.IsMatch(text, L + "web-?rip" + R, I))
        {
            r.Source = "WEBRip";
        }
        else if (Regex.IsMatch(text, L + "web" + R, I))
        {
            r.Source = "WEB-DL";
        }
        else if (Regex.IsMatch(text, L + "(blu-?ray|bdrip|brrip|bd25|bd50|bdremux)" + R, I))
        {
            r.Source = "BluRay";
        }
        else if (Regex.IsMatch(text, L + "hdtv" + R, I))
        {
            r.Source = "HDTV";
        }
        else if (Regex.IsMatch(text, L + "(dvdrip|dvd)" + R, I))
        {
            r.Source = "DVDRip";
        }
        else if (Regex.IsMatch(text, L + "hdrip" + R, I))
        {
            r.Source = "HDRip";
        }

        // Codec
        r.Codec =
            Regex.IsMatch(text, L + "(x265|h\\.?265|hevc)" + R, I) ? "HEVC" :
            Regex.IsMatch(text, L + "av1" + R, I) ? "AV1" :
            Regex.IsMatch(text, L + "(x264|h\\.?264|avc)" + R, I) ? "H.264" :
            Regex.IsMatch(text, L + "(xvid|divx)" + R, I) ? "XviD" : string.Empty;

        // HDR
        var dv = Regex.IsMatch(text, L + "(dv|dovi|dolby[ .-]?vision)" + R, I);
        var hdrp = Regex.IsMatch(text, "hdr10(\\+|plus)", I);
        var hdr10 = Regex.IsMatch(text, L + "hdr10" + R, I);
        var hdr = Regex.IsMatch(text, L + "hdr" + R, I);
        r.Hdr = dv && (hdrp || hdr10 || hdr) ? "DV HDR" : dv ? "DV" : hdrp ? "HDR10+" : hdr10 ? "HDR10" : hdr ? "HDR" : "SDR";

        // Bit depth
        r.BitDepth = Regex.IsMatch(text, L + "(10-?bit|10bits|hi10p?)" + R, I) || r.Hdr != "SDR" ? 10 : 8;

        // Audio
        var audio = new List<string>();
        if (Regex.IsMatch(text, "truehd", I))
        {
            audio.Add("TrueHD");
        }

        if (Regex.IsMatch(text, L + "dts-?hd([ .-]?ma)?" + R + "|dts-?x" + R, I))
        {
            audio.Add(Regex.IsMatch(text, "dts-?x" + R, I) ? "DTS:X" : "DTS-HD MA");
        }
        else if (Regex.IsMatch(text, L + "dts" + R, I))
        {
            audio.Add("DTS");
        }

        if (Regex.IsMatch(text, L + "(ddp|dd\\+|eac3|e-ac-3|ddp5|ddp2|ddp7)", I))
        {
            audio.Add("DD+");
        }
        else if (Regex.IsMatch(text, L + "(dd|ac3|dd5|dd2)(?![a-z])", I))
        {
            audio.Add("DD");
        }

        if (Regex.IsMatch(text, L + "aac", I))
        {
            audio.Add("AAC");
        }

        if (Regex.IsMatch(text, L + "flac" + R, I))
        {
            audio.Add("FLAC");
        }

        if (Regex.IsMatch(text, L + "opus" + R, I))
        {
            audio.Add("Opus");
        }

        var ch = Regex.Match(text, @"(?<![0-9])([257])[ .]?([01])(?![0-9])");
        if (ch.Success && audio.Count > 0)
        {
            audio.Add(ch.Groups[1].Value + "." + ch.Groups[2].Value);
        }

        if (Regex.IsMatch(text, "atmos", I))
        {
            audio.Add("Atmos");
        }

        r.Audio = string.Join(' ', audio);

        // Languages: from the name, then Torrentio's flags
        foreach (var (label, rx) in LangWords)
        {
            if (rx.IsMatch(text) && !r.Languages.Contains(label))
            {
                r.Languages.Add(label);
            }
        }

        foreach (var code in Flags(flagLine))
        {
            if (FlagLang.TryGetValue(code, out var lang))
            {
                // India's flag is used for every Indian language; skip it if the name already said which.
                if (lang == "Ind" && r.Languages.Any(l => l is "Tam" or "Mal" or "Tel" or "Hin" or "Kan" or "Ben" or "Mar"))
                {
                    continue;
                }

                if (!r.Languages.Contains(lang))
                {
                    r.Languages.Add(lang);
                }
            }
        }

        if (flagLine != null && flagLine.Contains("Multi", StringComparison.OrdinalIgnoreCase) && !r.Languages.Contains("Multi"))
        {
            r.Languages.Add("Multi");
        }

        // Subtitles
        r.Subtitles =
            Regex.IsMatch(text, L + "(esubs?|eng[ .-]?subs?)" + R, I) ? "ESub" :
            Regex.IsMatch(text, L + "(msubs?|multi[ .-]?subs?)" + R, I) ? "MSub" :
            Regex.IsMatch(text, L + "(subs?|subbed|subtitles?)" + R, I) ? "Subs" : string.Empty;

        // File type
        var ext = Regex.Match(r.FileName.Length > 0 ? r.FileName : r.Name, @"\.(mkv|mp4|avi|m4v|ts|m2ts|webm|mov|wmv)$", I);
        r.Container = ext.Success ? ext.Groups[1].Value.ToUpperInvariant() : string.Empty;

        // Speed from seeders
        r.Speed = r.Seeders >= 50 ? "Fast" : r.Seeders >= 10 ? "Good" : "Slow";

        r.Size = r.SizeBytes > 0 ? FormatSize(r.SizeBytes) : string.Empty;
        r.Summary = string.Join(" · ", new[]
        {
            r.Label.Length > 0 ? r.Label : null,
            r.IsCam ? "⚠️ CAM/TS" : r.Source,
            r.Quality == "?" ? null : r.Quality,
            r.Codec.Length > 0 ? r.Codec : null,
            r.Hdr,
            r.BitDepth + "-bit",
            r.Audio.Length > 0 ? r.Audio : null,
            r.Languages.Count > 0 ? string.Join("/", r.Languages) : null,
            r.Subtitles.Length > 0 ? r.Subtitles : null,
            r.Size.Length > 0 ? r.Size : null,
        }.Where(s => !string.IsNullOrEmpty(s)));
    }

    /// <summary>
    /// "Best" order: CAM/TS last, 10-bit first, then 1080p → 4K → 720p → 480p, then smallest file, then most seeders.
    /// </summary>
    public static List<ReleaseInfo> SortBest(IEnumerable<ReleaseInfo> list) =>
        list.OrderBy(r => r.IsCam ? 1 : 0)
            .ThenBy(r => r.BitDepth == 10 ? 0 : 1)
            .ThenBy(r => r.Quality switch { "1080p" => 0, "4K" => 1, "720p" => 2, "480p" => 3, _ => 4 })
            .ThenBy(r => r.SizeBytes <= 0 ? long.MaxValue : r.SizeBytes)
            .ThenByDescending(r => r.Seeders)
            .ToList();

    /// <summary>Parses "15.2 GB" style sizes.</summary>
    public static long ParseSize(string? s)
    {
        if (string.IsNullOrEmpty(s))
        {
            return 0;
        }

        var m = Regex.Match(s, @"([\d.,]+)\s*(TB|GB|MB|KB)", I);
        if (!m.Success || !double.TryParse(m.Groups[1].Value.Replace(",", string.Empty, StringComparison.Ordinal), NumberStyles.Float, CultureInfo.InvariantCulture, out var n))
        {
            return 0;
        }

        var mult = m.Groups[2].Value.ToUpperInvariant() switch
        {
            "TB" => 1024d * 1024 * 1024 * 1024,
            "GB" => 1024d * 1024 * 1024,
            "MB" => 1024d * 1024,
            _ => 1024d,
        };
        return (long)(n * mult);
    }

    /// <summary>Formats bytes as "2.4 GB".</summary>
    public static string FormatSize(long b) =>
        b >= 1L << 40 ? (b / (double)(1L << 40)).ToString("0.00", CultureInfo.InvariantCulture) + " TB" :
        b >= 1L << 30 ? (b / (double)(1L << 30)).ToString("0.0", CultureInfo.InvariantCulture) + " GB" :
        (b / (double)(1L << 20)).ToString("0", CultureInfo.InvariantCulture) + " MB";

    private static IEnumerable<string> Flags(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            yield break;
        }

        // Regional indicator letters come in pairs: 🇬🇧 = G + B.
        var runes = line.EnumerateRunes().ToList();
        for (var i = 0; i + 1 < runes.Count; i++)
        {
            int a = runes[i].Value, b = runes[i + 1].Value;
            if (a is >= 0x1F1E6 and <= 0x1F1FF && b is >= 0x1F1E6 and <= 0x1F1FF)
            {
                var sb = new StringBuilder(2);
                sb.Append((char)('A' + (a - 0x1F1E6)));
                sb.Append((char)('A' + (b - 0x1F1E6)));
                yield return sb.ToString();
                i++;
            }
        }
    }
}
