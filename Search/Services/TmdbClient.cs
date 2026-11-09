using MediaBrowser.Common.Net;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Plugin = PhoenicxTorBox.Plugin;

namespace TorviaSearch.Services;

/// <summary>Talks to TMDB. Uses Jellyfin's HTTP(S)_PROXY (your WARP proxy) like the rest of the server.</summary>
public class TmdbClient
{
    private const string Base = "https://api.themoviedb.org/3";
    private const string Img = "https://image.tmdb.org/t/p/";
    private readonly IHttpClientFactory _http;

    /// <summary>Initializes a new instance of the <see cref="TmdbClient"/> class.</summary>
    public TmdbClient(IHttpClientFactory http) => _http = http;

    /// <summary>Searches by name, TMDB number, TMDB link, IMDb id or IMDb link.</summary>
    public async Task<List<TitleResult>> SearchAsync(string query, CancellationToken ct)
    {
        query = query.Trim();
        var results = new List<TitleResult>();
        if (query.Length == 0)
        {
            return results;
        }

        // TMDB link: themoviedb.org/movie/603-the-matrix or /tv/340453
        var link = Regex.Match(query, @"themoviedb\.org/(movie|tv)/(\d+)", RegexOptions.IgnoreCase);
        if (link.Success)
        {
            var one = await GetBasicAsync(link.Groups[1].Value.ToLowerInvariant(), int.Parse(link.Groups[2].Value, CultureInfo.InvariantCulture), ct).ConfigureAwait(false);
            if (one != null)
            {
                results.Add(one);
            }

            return results;
        }

        // IMDb id or link
        var imdb = Regex.Match(query, @"\b(tt\d{5,10})\b", RegexOptions.IgnoreCase);
        if (imdb.Success)
        {
            using var doc = await GetAsync($"/find/{imdb.Groups[1].Value.ToLowerInvariant()}?external_source=imdb_id", ct).ConfigureAwait(false);
            foreach (var (prop, type) in new[] { ("movie_results", "movie"), ("tv_results", "tv") })
            {
                if (doc.RootElement.TryGetProperty(prop, out var arr))
                {
                    results.AddRange(arr.EnumerateArray().Select(e => Map(e, type)));
                }
            }

            return results;
        }

        // Plain number: try both a movie and a TV id
        if (Regex.IsMatch(query, @"^\d{1,9}$"))
        {
            var id = int.Parse(query, CultureInfo.InvariantCulture);
            var movie = GetBasicAsync("movie", id, ct);
            var tv = GetBasicAsync("tv", id, ct);
            foreach (var r in await Task.WhenAll(movie, tv).ConfigureAwait(false))
            {
                if (r != null)
                {
                    results.Add(r);
                }
            }

            if (results.Count > 0)
            {
                return results;
            }
        }

        // Name (optionally "Name 2021")
        var q = query;
        string yearPart = string.Empty;
        var y = Regex.Match(query, @"^(.*\S)\s+\(?((?:19|20)\d{2})\)?$");
        if (y.Success)
        {
            q = y.Groups[1].Value;
            yearPart = y.Groups[2].Value;
        }

        using (var doc = await GetAsync($"/search/multi?include_adult=false&query={Uri.EscapeDataString(q)}", ct).ConfigureAwait(false))
        {
            foreach (var e in doc.RootElement.GetProperty("results").EnumerateArray())
            {
                var mt = e.TryGetProperty("media_type", out var m) ? m.GetString() : null;
                if (mt is "movie" or "tv")
                {
                    results.Add(Map(e, mt));
                }
            }
        }

        if (yearPart.Length > 0)
        {
            var yr = int.Parse(yearPart, CultureInfo.InvariantCulture);
            results = results.OrderBy(r => r.Year == yr ? 0 : 1).ToList();
        }

        return results.Take(20).ToList();
    }

    /// <summary>Gets a title with IMDb id and (for TV) seasons.</summary>
    public async Task<TitleDetails?> GetDetailsAsync(string type, int id, CancellationToken ct)
    {
        type = type == "tv" ? "tv" : "movie";
        JsonDocument doc;
        try
        {
            doc = await GetAsync($"/{type}/{id}?append_to_response=external_ids", ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null;
        }

        using (doc)
        {
            var root = doc.RootElement;
            var d = new TitleDetails { Title = Map(root, type) };
            if (root.TryGetProperty("external_ids", out var ext) && ext.TryGetProperty("imdb_id", out var im) && im.ValueKind == JsonValueKind.String)
            {
                d.ImdbId = im.GetString();
            }
            else if (root.TryGetProperty("imdb_id", out var im2) && im2.ValueKind == JsonValueKind.String)
            {
                d.ImdbId = im2.GetString();
            }

            if (type == "tv" && root.TryGetProperty("seasons", out var seasons))
            {
                foreach (var s in seasons.EnumerateArray())
                {
                    var n = s.GetProperty("season_number").GetInt32();
                    var count = s.TryGetProperty("episode_count", out var ec) && ec.ValueKind == JsonValueKind.Number ? ec.GetInt32() : 0;
                    if (n < 1 || count < 1)
                    {
                        continue; // skip "Specials" and empty seasons
                    }

                    d.Seasons.Add(new SeasonInfo
                    {
                        Season = n,
                        Name = Str(s, "name") ?? $"Season {n}",
                        Episodes = count,
                        Year = YearOf(Str(s, "air_date")),
                    });
                }
            }

            return d;
        }
    }

    private async Task<TitleResult?> GetBasicAsync(string type, int id, CancellationToken ct)
    {
        try
        {
            using var doc = await GetAsync($"/{type}/{id}", ct).ConfigureAwait(false);
            return Map(doc.RootElement, type);
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static TitleResult Map(JsonElement e, string type)
    {
        var title = type == "tv" ? Str(e, "name") ?? Str(e, "original_name") : Str(e, "title") ?? Str(e, "original_title");
        var date = type == "tv" ? Str(e, "first_air_date") : Str(e, "release_date");
        var poster = Str(e, "poster_path");
        var back = Str(e, "backdrop_path");
        return new TitleResult
        {
            Type = type,
            TmdbId = e.GetProperty("id").GetInt32(),
            Title = title ?? "?",
            Year = YearOf(date),
            Overview = Str(e, "overview") ?? string.Empty,
            Poster = string.IsNullOrEmpty(poster) ? null : Img + "w342" + poster,
            Backdrop = string.IsNullOrEmpty(back) ? null : Img + "w780" + back,
            Rating = e.TryGetProperty("vote_average", out var v) && v.ValueKind == JsonValueKind.Number ? Math.Round(v.GetDouble(), 1) : null,
        };
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? YearOf(string? date) =>
        date != null && date.Length >= 4 && int.TryParse(date.AsSpan(0, 4), NumberStyles.None, CultureInfo.InvariantCulture, out var y) ? y : null;

    private async Task<JsonDocument> GetAsync(string path, CancellationToken ct)
    {
        var key = Plugin.Instance?.Configuration.TmdbApiKey?.Trim() ?? string.Empty;
        if (key.Length == 0)
        {
            throw new InvalidOperationException("The TMDB API key is not set in Torvia Search settings.");
        }

        var url = Base + path;
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        // A v4 "read access token" is a long JWT; a v3 key is 32 hex characters.
        if (key.StartsWith("ey", StringComparison.Ordinal) && key.Length > 60)
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
        else
        {
            req.RequestUri = new Uri(url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + "api_key=" + Uri.EscapeDataString(key));
        }

        var c = _http.CreateClient(NamedClient.Default);
        using var res = await c.SendAsync(req, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"TMDB answered {(int)res.StatusCode}", null, res.StatusCode);
        }

        var stream = await res.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
