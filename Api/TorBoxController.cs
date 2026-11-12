using MediaBrowser.Common.Api;
using MediaBrowser.Model.Tasks;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using PhoenicxTorBox.Library;
using PhoenicxTorBox.Tasks;
using PhoenicxTorBox.TorBox;

namespace PhoenicxTorBox.Api;

/// <summary>Play links and the plugin page's buttons.</summary>
[ApiController]
[Route("PhoenicxTorBox")]
public class TorBoxController : ControllerBase
{
    private readonly TorBoxClient _torbox;
    private readonly LibrarySync _sync;
    private readonly ITaskManager _tasks;
    private readonly ILogger<TorBoxController> _log;

    /// <summary>
    /// Initializes a new instance of the <see cref="TorBoxController"/> class.
    /// </summary>
    public TorBoxController(TorBoxClient torbox, LibrarySync sync, ITaskManager tasks, ILogger<TorBoxController> log)
    {
        _torbox = torbox;
        _sync = sync;
        _tasks = tasks;
        _log = log;
    }

    /// <summary>
    /// The link inside every .strm file. Sends the player on to a fresh TorBox link, so the video
    /// itself comes from TorBox, not through this server. The signature stops guessed links.
    /// </summary>
    [HttpGet("Play/{t}/{itemId}/{fileId}")]
    [HttpHead("Play/{t}/{itemId}/{fileId}")]
    [AllowAnonymous]
    public async Task<ActionResult> Play(
        [FromRoute] string t,
        [FromRoute] long itemId,
        [FromRoute] long fileId,
        [FromQuery] string? sig,
        CancellationToken ct)
    {
        t = t.ToLowerInvariant();
        if (TorBoxClient.FromShort(t) is not { } type || !PlayLinks.Verify(t, itemId, fileId, sig))
        {
            return NotFound();
        }

        try
        {
            var link = await _torbox.GetStreamLinkAsync(type, itemId, fileId, ct, null, ViewerIp.Of(HttpContext)).ConfigureAwait(false);
            return Redirect(link);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning("Torvia: no link for {T}/{Item}/{File}: {Msg}", t, itemId, fileId, ex.Message);
            return StatusCode(503, "TorBox is not giving a link for this file right now (maintenance, or the file was removed from TorBox). Try again later.");
        }
    }

    /// <summary>Folders, last sync result. For the plugin page.</summary>
    [HttpGet("Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult<object> Status()
    {
        var p = Plugin.Instance!;
        return new
        {
            moviesPath = p.MoviesPath,
            showsPath = p.ShowsPath,
            byTag = p.Configuration.FoldersByTag,
            hasKey = !string.IsNullOrWhiteSpace(p.Configuration.TorBoxApiKey),
            last = _sync.Status,
            refreshKey = PlayLinks.RefreshKey(),
            stats = PhoenicxTorBox.Stats.StreamStats.Instance?.Totals,
        };
    }

    /// <summary>Starts a sync now (as the scheduled task, so it shows in the dashboard).</summary>
    [HttpPost("Sync")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SyncNow()
    {
        _tasks.CancelIfRunningAndQueue<SyncTask>();
        return NoContent();
    }

    /// <summary>
    /// The refresh link: opening it in any browser starts a sync, without signing in. It only works
    /// with the key shown on the settings page. With check=1 it only reports the last sync.
    /// </summary>
    [HttpGet("Refresh")]
    [AllowAnonymous]
    public ActionResult Refresh([FromQuery] string? key, [FromQuery] int check = 0)
    {
        if (string.IsNullOrEmpty(key)
            || !System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.ASCII.GetBytes(PlayLinks.RefreshKey()),
                System.Text.Encoding.ASCII.GetBytes(key.Trim().ToLowerInvariant())))
        {
            return NotFound();
        }

        Response.Headers.CacheControl = "no-store";
        if (check != 0)
        {
            var last = _sync.Status;
            return new JsonResult(new { finishedUtc = last.FinishedUtc, ok = last.Ok, message = last.Message, note = last.Note });
        }

        // Opening the link again and again starts at most one sync every 30 seconds.
        var now = DateTime.UtcNow;
        lock (RefreshLock)
        {
            if (now - _lastRefresh >= TimeSpan.FromSeconds(30))
            {
                _lastRefresh = now;
                _tasks.CancelIfRunningAndQueue<SyncTask>();
            }
        }

        return Content(RefreshPage.Replace("{{since}}", _lastRefresh.ToString("o"), StringComparison.Ordinal), "text/html; charset=utf-8");
    }

    private static readonly object RefreshLock = new();
    private static DateTime _lastRefresh = DateTime.MinValue;

    private const string RefreshPage = """
        <!DOCTYPE html><html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
        <meta name="robots" content="noindex"><title>Torvia</title>
        <style>body{margin:0;min-height:100vh;display:grid;place-items:center;background:#0f1220;color:#f4f4fa;font-family:system-ui,Segoe UI,sans-serif}
        .c{max-width:440px;margin:1rem;padding:1.6rem 1.8rem;border-radius:16px;background:rgba(255,255,255,.05);border:1px solid rgba(255,255,255,.1);text-align:center}
        h1{font-size:1.3rem;margin:0 0 .6rem}p{margin:.4rem 0;color:rgba(255,255,255,.7);line-height:1.5}.ok{color:#6ee7b7}.bad{color:#fca5a5}</style></head>
        <body><div class="c"><h1 id="h">⟳ Torvia is syncing…</h1><p id="m">Checking TorBox for new and removed titles.</p></div>
        <script>
        (function () {
          var since = new Date('{{since}}').getTime(), tries = 0;
          function check() {
            fetch(location.pathname + location.search + '&check=1', { cache: 'no-store' }).then(function (r) { return r.json(); }).then(function (s) {
              if (s.finishedUtc && new Date(s.finishedUtc).getTime() >= since) {
                document.getElementById('h').textContent = s.ok ? '✓ Sync finished' : '✕ Sync failed';
                document.getElementById('h').className = s.ok ? 'ok' : 'bad';
                document.getElementById('m').textContent = s.message || '';
              } else if (++tries < 100) { setTimeout(check, 3000); }
              else { document.getElementById('m').textContent = 'Still running. You can close this page.'; }
            }, function () { if (++tries < 100) setTimeout(check, 3000); });
          }
          setTimeout(check, 2000);
        })();
        </script></body></html>
        """;

    /// <summary>Checks the saved API key.</summary>
    [HttpGet("Test")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult<object>> Test(CancellationToken ct)
    {
        try
        {
            return new { ok = true, message = await _torbox.TestAsync(ct).ConfigureAwait(false) };
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new { ok = false, message = ex.Message };
        }
    }
}
