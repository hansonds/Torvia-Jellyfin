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

    /// <summary>
    /// Releases from the main Torrentio address and, when one is set, the second address, asked
    /// at the same time and joined into one list (each torrent once, the main address first).
    /// If one of the two fails, the other's results are still returned.
    /// </summary>
    public async Task<List<(ReleaseInfo Release, string? Flags)>> GetAsync(string imdbId, int? season, int? episode, CancellationToken ct)
    {
        var cfg = Plugin.Instance?.Configuration;
        var first = Clean(cfg?.TorrentioUrl);
        if (first.Length == 0)
        {
            first = "https://torrentio.strem.fun";
        }

        var second = Clean(cfg?.TorrentioUrl2);
        if (second.Length == 0 || string.Equals(second, first, StringComparison.OrdinalIgnoreCase))
        {
            return await GetFromAsync(first, imdbId, season, episode, ct).ConfigureAwait(false);
        }

        var a = GetFromAsync(first, imdbId, season, episode, ct);
        var b = GetFromAsync(second, imdbId, season, episode, ct);
        try
        {
            await Task.WhenAll(a, b).ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
            // Looked at one by one below.
        }

        ct.ThrowIfCancellationRequested();
        if (!a.IsCompletedSuccessfully && !b.IsCompletedSuccessfully)
        {
            // Both failed: report the main address's reason.
            return await a.ConfigureAwait(false);
        }

        var list = new List<(ReleaseInfo, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in new[] { a, b })
        {
            if (!t.IsCompletedSuccessfully)
            {
                continue;
            }

            foreach (var item in t.Result)
            {
                if (seen.Add(item.Release.Hash))
                {
                    list.Add(item);
                }
            }
        }

        return list;
    }

    private static string Clean(string? address)
    {
        var u = (address ?? string.Empty).Trim().TrimEnd('/');
        if (u.EndsWith("/manifest.json", StringComparison.OrdinalIgnoreCase))
        {
            u = u[..^"/manifest.json".Length];
        }

        // A link copied from Stremio starts with stremio://
        if (u.StartsWith("stremio://", StringComparison.OrdinalIgnoreCase))
        {
            u = "https://" + u["stremio://".Length..];
        }

        return u;
    }

    /// <summary>Movie: /stream/movie/tt….json — TV: /stream/series/tt…:season:episode.json.</summary>
    private async Task<List<(ReleaseInfo Release, string? Flags)>> GetFromAsync(string baseUrl, string imdbId, int? season, int? episode, CancellationToken ct)
    {

        var url = season is null
            ? $"{baseUrl}/stream/movie/{imdbId}.json"
            : $"{baseUrl}/stream/series/{imdbId}:{season}:{episode ?? 1}.json";

        var c = _http.CreateClient(NamedClient.Default);
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "Mozilla/5.0 (Windows NT 10.0; Win64; x64) TorviaSearch/1.0");
        HttpResponseMessage res;
        try
        {
            res = await c.SendAsync(req, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException($"Torrentio can't be reached from this server ({ex.Message}). " + Blocked);
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new InvalidOperationException("Torrentio didn't answer in time. " + Blocked);
        }

        using var _ = res;
        if (!res.IsSuccessStatusCode)
        {
            var code = (int)res.StatusCode;
            throw new InvalidOperationException(code is 403 or 429 or 503 or 520 or 521 or 522 or 525
                ? $"Torrentio refused this server (answer {code}). " + Blocked
                : $"Torrentio answered {code}. Try again in a minute.");
        }

        await using var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            // A web page instead of data: usually a block / "checking your browser" page.
            throw new InvalidOperationException("Torrentio sent back a web page instead of results. " + Blocked);
        }

        using var __ = doc;

        var list = new List<(ReleaseInfo, string?)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
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
            // Other Torrentio-style sources put an icon in front of the name ("📂 Name"): drop it.
            var names = (infoIdx < 0 ? lines : lines[..infoIdx])
                .Select(l => LeadingIcons.Replace(l, string.Empty)).Where(l => l.Length > 0).ToArray();
            var info = infoIdx < 0 ? string.Empty : lines[infoIdx];
            var flags = infoIdx < 0 || infoIdx + 1 >= lines.Length ? null : string.Join(' ', lines[(infoIdx + 1)..]);

            string? hintFile = null;
            long hintSize = 0;
            if (s.TryGetProperty("behaviorHints", out var bh))
            {
                hintFile = Str(bh, "filename");
                if (bh.TryGetProperty("videoSize", out var vs) && vs.ValueKind == JsonValueKind.Number)
                {
                    hintSize = vs.TryGetInt64(out var size) ? size : (long)vs.GetDouble();
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

    private static readonly Regex LeadingIcons = new(@"^[^\p{L}\p{N}\[\(]+", RegexOptions.Compiled);

    private const string Blocked =
        "Torrentio blocks many cloud / VPS servers. Run the server's traffic through a proxy (e.g. Cloudflare WARP) " +
        "or enter a self-hosted Torrentio address on the Torvia Search tab.";

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
