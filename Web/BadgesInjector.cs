using System.Text;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;

namespace PhoenicxTorBox.Web;

/// <summary>
/// Adds the media badges script to Jellyfin's web page (index.html) as it is sent, in memory.
/// Nothing on disk is changed, so it survives Jellyfin updates and needs no write access.
/// Works for every client that loads the server's web page: browsers, Jellyfin Media Player and
/// the Android app.
/// </summary>
public sealed class BadgesStartupFilter : IStartupFilter
{
    private static readonly Regex IndexPath = new(@"/web/(index\.html)?$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <inheritdoc />
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            app.Use(InjectAsync);
            next(app);
        };
    }

    private static async Task InjectAsync(HttpContext ctx, Func<Task> next)
    {
        var path = ctx.Request.Path.Value ?? string.Empty;
        if (Plugin.Instance?.Configuration.ShowMediaBadges != true
            || !HttpMethods.IsGet(ctx.Request.Method)
            || !IndexPath.IsMatch(path))
        {
            await next().ConfigureAwait(false);
            return;
        }

        // Get the plain, full page (not compressed, not "unchanged since last time").
        ctx.Request.Headers.Remove("Accept-Encoding");
        ctx.Request.Headers.Remove("If-None-Match");
        ctx.Request.Headers.Remove("If-Modified-Since");
        ctx.Request.Headers.Remove("Range");

        var original = ctx.Response.Body;
        using var buffer = new MemoryStream();
        ctx.Response.Body = buffer;
        try
        {
            await next().ConfigureAwait(false);
        }
        finally
        {
            ctx.Response.Body = original;
        }

        buffer.Position = 0;
        var isHtml = ctx.Response.StatusCode == StatusCodes.Status200OK
            && (ctx.Response.ContentType?.Contains("text/html", StringComparison.OrdinalIgnoreCase) ?? false)
            && !ctx.Response.Headers.ContainsKey("Content-Encoding");
        if (!isHtml)
        {
            await buffer.CopyToAsync(original).ConfigureAwait(false);
            return;
        }

        var html = Encoding.UTF8.GetString(buffer.ToArray());
        if (!html.Contains("Torvia/Badges.js", StringComparison.OrdinalIgnoreCase))
        {
            // Address of the script on this server, base URL included ("/jellyfin/web/" → "/jellyfin").
            var prefix = path[..path.LastIndexOf("/web/", StringComparison.OrdinalIgnoreCase)];
            var version = typeof(BadgesStartupFilter).Assembly.GetName().Version?.ToString() ?? "1";
            // Default badge styles first in <head>, so any custom CSS added later wins.
            var css = $"<link rel=\"stylesheet\" href=\"{prefix}/Torvia/Badges.css?v={version}\">";
            var head = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
            html = head >= 0 ? html.Insert(head, css) : css + html;

            var tag = $"<script defer src=\"{prefix}/Torvia/Badges.js?v={version}\"></script>";
            var at = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            html = at >= 0 ? html.Insert(at, tag) : html + tag;
        }

        var bytes = Encoding.UTF8.GetBytes(html);
        ctx.Response.ContentLength = bytes.Length;
        ctx.Response.Headers.Remove("ETag");
        ctx.Response.Headers.Remove("Last-Modified");
        await original.WriteAsync(bytes, ctx.RequestAborted).ConfigureAwait(false);
    }
}
