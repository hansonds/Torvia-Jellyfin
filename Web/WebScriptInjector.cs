using MediaBrowser.Controller.Configuration;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Web;

/// <summary>
/// Puts Torvia's middleware in front of Jellyfin's own, so the Search &amp; Add button script can be
/// added to the web client's index.html as it is sent. Nothing on disk is changed.
/// </summary>
public class WebScriptInjector : IStartupFilter
{
    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.UseMiddleware<WebScriptMiddleware>();
            next(app);
        };
    }
}

/// <summary>
/// Serves the Jellyfin web client's index.html with one extra script tag (the Search &amp; Add button).
/// Anything it can't handle goes on to Jellyfin unchanged.
/// </summary>
public class WebScriptMiddleware
{
    private const string Marker = "TorviaSearch/HomeButton.js";
    private readonly RequestDelegate _next;
    private readonly IServerConfigurationManager _config;
    private readonly ILogger<WebScriptMiddleware> _log;
    private readonly object _lock = new();
    private string? _cachedHtml;
    private DateTime _cachedStamp;
    private bool _warned;

    /// <summary>Initializes a new instance of the <see cref="WebScriptMiddleware"/> class.</summary>
    public WebScriptMiddleware(RequestDelegate next, IServerConfigurationManager config, ILogger<WebScriptMiddleware> log)
    {
        _next = next;
        _config = config;
        _log = log;
    }

    /// <summary>Handles one request.</summary>
    public async Task InvokeAsync(HttpContext context)
    {
        var path = context.Request.Path.Value ?? string.Empty;
        int at = WebIndexAt(path);
        if (at < 0
            || !HttpMethods.IsGet(context.Request.Method)
            || Plugin.Instance?.Configuration.HomeButton != true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        string? html = null;
        try
        {
            html = Load();
        }
        catch (Exception ex)
        {
            if (!_warned)
            {
                _warned = true;
                _log.LogWarning(ex, "Torvia: could not add the Search & Add button to the web client");
            }
        }

        if (html == null)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        // The part of the path before /web/ (the server's base URL, if it has one).
        var prefix = path.Substring(0, at);
        var version = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1";
        var tag = "<script src=\"" + prefix + "/" + Marker + "?v=" + version + "\" defer></script>";
        var i = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
        html = i >= 0 ? html.Insert(i, tag) : html + tag;

        context.Response.StatusCode = 200;
        context.Response.ContentType = "text/html; charset=utf-8";
        context.Response.Headers.CacheControl = "no-cache";
        await context.Response.WriteAsync(html).ConfigureAwait(false);
    }

    /// <summary>Where "/web/" starts if this is a request for the web client's main page; otherwise -1.</summary>
    private static int WebIndexAt(string path)
    {
        if (path.EndsWith("/web/index.html", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length - "/web/index.html".Length;
        }

        if (path.EndsWith("/web/", StringComparison.OrdinalIgnoreCase))
        {
            return path.Length - "/web/".Length;
        }

        return -1;
    }

    private string? Load()
    {
        var webPath = _config.ApplicationPaths.WebPath;
        if (string.IsNullOrEmpty(webPath))
        {
            return null;
        }

        var file = Path.Combine(webPath, "index.html");
        if (!File.Exists(file))
        {
            return null;
        }

        var stamp = File.GetLastWriteTimeUtc(file);
        lock (_lock)
        {
            if (_cachedHtml != null && stamp == _cachedStamp)
            {
                return _cachedHtml;
            }
        }

        var text = File.ReadAllText(file);
        if (text.Contains(Marker, StringComparison.OrdinalIgnoreCase))
        {
            return null; // already there (added some other way); leave the page alone
        }

        lock (_lock)
        {
            _cachedHtml = text;
            _cachedStamp = stamp;
        }

        return text;
    }
}
