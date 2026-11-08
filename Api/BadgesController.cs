using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace PhoenicxTorBox.Api;

/// <summary>Serves the media badges script added to Jellyfin's web page.</summary>
[ApiController]
[Route("Torvia")]
public class BadgesController : ControllerBase
{
    private static readonly Lazy<byte[]> Script = new(() => Resource("phoenicx-media-badges.js"));
    private static readonly Lazy<byte[]> Styles = new(() => Resource("phoenicx-media-badges.css"));

    /// <summary>The badges script (empty when "Show media badges" is off).</summary>
    [HttpGet("Badges.js")]
    [AllowAnonymous]
    public ActionResult GetBadges()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        if (Plugin.Instance?.Configuration.ShowMediaBadges != true)
        {
            return Content("/* Torvia media badges are turned off */", "application/javascript; charset=utf-8");
        }

        return File(Script.Value, "application/javascript; charset=utf-8");
    }

    /// <summary>Default badge styles (custom CSS such as a theme overrides them).</summary>
    [HttpGet("Badges.css")]
    [AllowAnonymous]
    public ActionResult GetBadgeStyles()
    {
        Response.Headers.CacheControl = "public, max-age=3600";
        if (Plugin.Instance?.Configuration.ShowMediaBadges != true)
        {
            return Content("/* Torvia media badges are turned off */", "text/css; charset=utf-8");
        }

        return File(Styles.Value, "text/css; charset=utf-8");
    }

    private static byte[] Resource(string fileName)
    {
        var asm = typeof(BadgesController).Assembly;
        var name = asm.GetManifestResourceNames().First(n => n.EndsWith(fileName, StringComparison.OrdinalIgnoreCase));
        using var s = asm.GetManifestResourceStream(name)!;
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        return ms.ToArray();
    }
}
