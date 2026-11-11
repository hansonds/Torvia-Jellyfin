using MediaBrowser.Common.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>The TorBox calls the add flow needs: checkcached, createtorrent, edittorrent, mylist, requestdl.</summary>
public class TorBoxApi
{
    private const string Base = "https://api.torbox.app/v1/api/torrents";
    private readonly IHttpClientFactory _http;

    /// <summary>Initializes a new instance of the <see cref="TorBoxApi"/> class.</summary>
    public TorBoxApi(IHttpClientFactory http) => _http = http;

    /// <summary>Gets the TorBox API key saved on Torvia's main settings tab.</summary>
    public static string? ResolveKey()
    {
        var key = Plugin.Instance?.Configuration.TorBoxApiKey?.Trim();
        return string.IsNullOrEmpty(key) ? null : key;
    }

    /// <summary>Returns the cached hashes (lowercase) with the torrent size TorBox reports (0 if unknown), 50 at a time.</summary>
    public async Task<Dictionary<string, long>> CheckCachedAsync(IEnumerable<string> hashes, CancellationToken ct)
    {
        var cached = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var c = Client();
        foreach (var chunk in hashes.Distinct(StringComparer.OrdinalIgnoreCase).Chunk(50))
        {
            using var doc = await GetJsonAsync(c, $"{Base}/checkcached?hash={string.Join(',', chunk)}&format=object&list_files=false", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data))
            {
                continue;
            }

            if (data.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in data.EnumerateObject())
                {
                    cached[p.Name.ToLowerInvariant()] = SizeOf(p.Value);
                }
            }
            else if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in data.EnumerateArray())
                {
                    if (e.TryGetProperty("hash", out var h) && h.GetString() is { } hs)
                    {
                        cached[hs.ToLowerInvariant()] = SizeOf(e);
                    }
                }
            }
        }

        return cached;
    }

    /// <summary>Finds a torrent already in the account by hash. Returns its id and tags.</summary>
    public async Task<(long Id, List<string> Tags)?> FindByHashAsync(string hash, CancellationToken ct)
    {
        var c = Client();
        for (var offset = 0; offset < 5000; offset += 1000)
        {
            using var doc = await GetJsonAsync(c, $"{Base}/mylist?bypass_cache=true&offset={offset}&limit=1000", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array)
            {
                return null;
            }

            var count = 0;
            foreach (var t in data.EnumerateArray())
            {
                count++;
                if (string.Equals(t.TryGetProperty("hash", out var h) ? h.GetString() : null, hash, StringComparison.OrdinalIgnoreCase))
                {
                    return (t.GetProperty("id").GetInt64(), Tags(t));
                }
            }

            if (count < 1000)
            {
                return null;
            }
        }

        return null;
    }

    /// <summary>Adds a cached magnet with the given name. Returns the torrent id.</summary>
    public async Task<long> CreateAsync(string hash, string name, CancellationToken ct)
    {
        var magnet = $"magnet:?xt=urn:btih:{hash}&dn={Uri.EscapeDataString(name)}";
        using var form = new MultipartFormDataContent
        {
            { new StringContent(magnet), "magnet" },
            { new StringContent(name), "name" },
            { new StringContent("true"), "add_only_if_cached" },
            { new StringContent("false"), "allow_zip" },
        };

        var c = Client();
        using var res = await c.PostAsync($"{Base}/createtorrent", form, ct).ConfigureAwait(false);
        using var doc = await ReadAsync(res, ct).ConfigureAwait(false);
        var root = doc.RootElement;
        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object && data.TryGetProperty("torrent_id", out var id))
        {
            return id.GetInt64();
        }

        throw new InvalidOperationException(Detail(root) ?? "TorBox did not add it.");
    }

    /// <summary>Sets the tags (and keeps the name).</summary>
    public async Task SetTagsAsync(long id, IEnumerable<string> tags, CancellationToken ct)
    {
        var c = Client();
        using var res = await c.PutAsJsonAsync($"{Base}/edittorrent", new { torrent_id = id, tags = tags.ToArray() }, ct).ConfigureAwait(false);
        using var doc = await ReadAsync(res, ct).ConfigureAwait(false);
    }

    /// <summary>Deletes a torrent from TorBox. Returns false if TorBox no longer has it.</summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        var c = Client();
        using var res = await c.PostAsJsonAsync($"{Base}/controltorrent", new { torrent_id = id, operation = "delete" }, ct).ConfigureAwait(false);
        try
        {
            using var doc = await ReadAsync(res, ct).ConfigureAwait(false);
            return true;
        }
        catch (InvalidOperationException ex) when ((int)res.StatusCode == 404 || ex.Message.Contains("not found", StringComparison.OrdinalIgnoreCase) || ex.Message.Contains("NOT_FOUND", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }
    }

    /// <summary>
    /// Whether this torrent is still in the TorBox account. Null when TorBox can't say right now
    /// (not reachable, busy, or an error on its side), which is not the same as "gone".
    /// </summary>
    public async Task<bool?> ExistsAsync(long id, CancellationToken ct)
    {
        try
        {
            using var res = await Client().GetAsync($"{Base}/mylist?id={id}&bypass_cache=true", ct).ConfigureAwait(false);
            var code = (int)res.StatusCode;
            if (code is 401 or 403 or 408 or 429)
            {
                return null;
            }

            var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            try
            {
                using var doc = JsonDocument.Parse(body);
                var root = doc.RootElement;
                if (root.ValueKind != JsonValueKind.Object)
                {
                    return null;
                }

                // TorBox's own answer: the download, or its "no such download" error.
                return res.IsSuccessStatusCode
                    && !(root.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False)
                    && root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object;
            }
            catch (JsonException)
            {
                // An error page from a gateway in front of TorBox (maintenance, overload).
                return null;
            }
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>
    /// Waits for the files to show up, picks the episode's file (or else the largest video) and returns a stream link.
    /// </summary>
    public async Task<string?> GetStreamLinkAsync(long id, int? season, int? episode, CancellationToken ct)
    {
        var c = Client();
        JsonElement? pick = null;
        for (var attempt = 0; attempt < 8 && pick == null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(2500, ct).ConfigureAwait(false);
            }

            using var doc = await GetJsonAsync(c, $"{Base}/mylist?id={id}&bypass_cache=true", ct).ConfigureAwait(false);
            if (!doc.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object
                || !data.TryGetProperty("files", out var files) || files.ValueKind != JsonValueKind.Array || files.GetArrayLength() == 0)
            {
                continue;
            }

            var videos = files.EnumerateArray()
                .Where(f => Regex.IsMatch(FileName(f), @"\.(mkv|mp4|avi|m4v|ts|m2ts|webm|mov)$", RegexOptions.IgnoreCase))
                .ToList();
            if (videos.Count == 0)
            {
                videos = files.EnumerateArray().ToList();
            }

            JsonElement? chosen = null;
            if (season != null && episode != null)
            {
                var rx = new Regex($@"S0*{season}[ .]?E0*{episode}(?!\d)|(?<!\d)0*{season}x0*{episode}(?!\d)", RegexOptions.IgnoreCase);
                foreach (var f in videos)
                {
                    if (rx.IsMatch(FileName(f)))
                    {
                        chosen = f;
                        break;
                    }
                }
            }

            chosen ??= videos.OrderByDescending(f => f.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0).First();
            pick = chosen.Value.Clone();
        }

        if (pick == null)
        {
            return null;
        }

        var fileId = pick.Value.GetProperty("id").GetInt64();
        var key = ResolveKey()!;
        using var link = await GetJsonAsync(c, $"{Base}/requestdl?token={Uri.EscapeDataString(key)}&torrent_id={id}&file_id={fileId}&zip_link=false&redirect=false", ct).ConfigureAwait(false);
        return link.RootElement.TryGetProperty("data", out var url) && url.ValueKind == JsonValueKind.String ? url.GetString() : null;
    }

    private static long SizeOf(JsonElement e) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number && s.TryGetInt64(out var v) ? v : 0;

    private static string FileName(JsonElement f) =>
        (f.TryGetProperty("short_name", out var s) ? s.GetString() : null)
        ?? (f.TryGetProperty("name", out var n) ? n.GetString() : null)
        ?? string.Empty;

    private static List<string> Tags(JsonElement t)
    {
        var list = new List<string>();
        if (t.TryGetProperty("tags", out var tags) && tags.ValueKind == JsonValueKind.Array)
        {
            foreach (var e in tags.EnumerateArray())
            {
                var v = e.ValueKind == JsonValueKind.String ? e.GetString()
                    : e.ValueKind == JsonValueKind.Object && e.TryGetProperty("name", out var nm) ? nm.GetString() : null;
                if (!string.IsNullOrWhiteSpace(v))
                {
                    list.Add(v!);
                }
            }
        }

        return list;
    }

    private HttpClient Client()
    {
        var key = ResolveKey() ?? throw new InvalidOperationException("No TorBox API key: add it on the Torvia tab of the settings page.");
        var c = _http.CreateClient(NamedClient.Default);
        c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return c;
    }

    private static async Task<JsonDocument> GetJsonAsync(HttpClient c, string url, CancellationToken ct)
    {
        using var res = await c.GetAsync(url, ct).ConfigureAwait(false);
        return await ReadAsync(res, ct).ConfigureAwait(false);
    }

    private static async Task<JsonDocument> ReadAsync(HttpResponseMessage res, CancellationToken ct)
    {
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"TorBox answered {(int)res.StatusCode}.");
        }

        if (!res.IsSuccessStatusCode || (doc.RootElement.TryGetProperty("success", out var ok) && ok.ValueKind == JsonValueKind.False))
        {
            var msg = Detail(doc.RootElement) ?? $"TorBox answered {(int)res.StatusCode}.";
            doc.Dispose();
            throw new InvalidOperationException(msg);
        }

        return doc;
    }

    private static string? Detail(JsonElement root) =>
        root.TryGetProperty("detail", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()
        : root.TryGetProperty("error", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
}
