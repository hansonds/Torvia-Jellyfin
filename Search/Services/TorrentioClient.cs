using MediaBrowser.Common.Net;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>Gets the list of releases for an IMDb id from Torrentio.</summary>
public class TorrentioClient
{
    private readonly IHttpClientFactory _http;

    /// <summary>Initializes a new instance of the <see cref="TorrentioClient"/> class.</summary>
    public TorrentioClient(IHttpClientFactory http) => _http = http;

    /// <summary>Movie: /stream/movie/tt….json — TV: /stream/series/tt…:season:episode.json.</summary>
    public async Task<List<(ReleaseInfo Release, string? Flags)>> GetAsync(string imdbId, int? season, int? episode, CancellationToken ct)
    {
        var baseUrl = (Plugin.Instance?.Configuration.TorrentioUrl ?? string.Empty).Trim().TrimEnd('/');
        if (baseUrl.Length == 0)
        {
            baseUrl = "https://torrentio.strem.fun";
        }

        if (baseUrl.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..^"/manifest.json".Length];
        }

        var url = season is null
            ? $"{baseUrl}/stream/movie/{imdbId}.json"
            : $"{baseUrl}/stream/series/{imdbId}:{season}:{episode ?? 1}.json";

        var c = _http.CreateClient(NamedClient.Default);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TorviaSearch/1.0");
        using var res = await c.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"Torrentio answered {(int)res.StatusCode}");
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        var list = new List<(ReleaseInfo, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("streams", out var streams))
        {
            return list;
        }

        foreach (var s in streams.EnumerateArray())
        {
            var hash = Str(s, "infoHash")?.ToLowerInvariant();
            if (string.IsNullOrEmpty(hash) || !Regex.IsMatch(hash, "^[0-9a-f]{40}$") || !seen.Add(hash))
            {
                continue;
            }

            // title: "Torrent.Name\nFile.Name.mkv\n👤 12 💾 2.3 GB ⚙️ Source\nMulti Audio / 🇬🇧 / 🇮🇳"
            var title = Str(s, "title") ?? Str(s, "description") ?? string.Empty;
            var lines = title.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var infoIdx = Array.FindIndex(lines, l => l.Contains("👤", StringComparison.Ordinal) || l.Contains("💾", StringComparison.Ordinal));
            var names = infoIdx < 0 ? lines : lines[..infoIdx];
            var info = infoIdx < 0 ? string.Empty : lines[infoIdx];
            var flags = infoIdx < 0 || infoIdx + 1 >= lines.Length ? null : string.Join(' ', lines[(infoIdx + 1)..]);

            string? hintFile = null;
            long hintSize = 0;
            if (s.TryGetProperty("behaviorHints", out var bh))
            {
                hintFile = Str(bh, "filename");
                if (bh.TryGetProperty("videoSize", out var vs) && vs.ValueKind == JsonValueKind.Number)
                {
                    hintSize = vs.GetInt64();
                }
            }

            var r = new ReleaseInfo
            {
                Hash = hash,
                Name = names.Length > 0 ? names[0] : hintFile ?? hash,
                FileName = hintFile ?? (names.Length > 1 ? names[^1] : names.Length > 0 ? names[0] : string.Empty),
            };

            var seed = Regex.Match(info, @"👤\s*(\d+)");
            if (seed.Success)
            {
                r.Seeders = int.Parse(seed.Groups[1].Value, CultureInfo.InvariantCulture);
            }

            r.SizeBytes = hintSize > 0 ? hintSize : ReleaseParser.ParseSize(Regex.Match(info, @"💾\s*([^⚙]+)").Groups[1].Value);

            // Torrentio's "name" has the quality on its second line (e.g. "Torrentio\n4k DV").
            var tag = Str(s, "name") ?? string.Empty;
            list.Add((r, flags + " " + tag.Replace('\n', ' ')));
        }

        return list;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
