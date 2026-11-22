using System.Security.Claims;
using System.Text.Json;
using MediaBrowser.Common.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Push;

/// <summary>
/// Push notifications for PHOENIXA. Every call needs a signed-in Jellyfin user; the Admin calls
/// are for the settings page and need an administrator.
/// </summary>
[ApiController]
[Route("TorviaPush")]
[Authorize]
public class PushController : ControllerBase
{
    /// <summary>PHOENIXA's Android package name.</summary>
    public const string PackageName = "org.phoenicx.nova";

    private readonly PushStore _store;
    private readonly PushNotifier _notifier;
    private readonly FcmSender _fcm;
    private readonly ILogger<PushController> _log;

    /// <summary>Initializes a new instance of the <see cref="PushController"/> class.</summary>
    public PushController(PushStore store, PushNotifier notifier, FcmSender fcm, ILogger<PushController> log)
    {
        _store = store;
        _notifier = notifier;
        _fcm = fcm;
        _log = log;
    }

    /// <summary>The Firebase settings the app needs to get its FCM token (never the service account).</summary>
    [HttpGet("Config")]
    public ActionResult Config()
    {
        Response.Headers.CacheControl = "no-store";
        if (_notifier.Firebase() is not { } fb)
        {
            return Ok(new { enabled = false });
        }

        return Ok(new { enabled = true, projectId = fb.ProjectId, appId = fb.AppId, apiKey = fb.ApiKey, senderId = fb.SenderId });
    }

    /// <summary>Registers this device's FCM token for the signed-in user.</summary>
    [HttpPost("Register")]
    public ActionResult Register([FromBody] JsonElement body)
    {
        var uid = UserId();
        var token = Str(body, "token");
        var deviceId = Str(body, "deviceId");
        if (uid == Guid.Empty)
        {
            return Unauthorized();
        }

        if (string.IsNullOrWhiteSpace(token) || token.Length > 4096 || string.IsNullOrWhiteSpace(deviceId) || deviceId.Length > 256)
        {
            return BadRequest(new { message = "token and deviceId are needed." });
        }

        _store.Register(new PushDevice
        {
            UserId = uid.ToString("N"),
            DeviceId = deviceId.Trim(),
            Token = token.Trim(),
            DeviceName = Cut(Str(body, "deviceName"), 100),
            AppVersion = Cut(Str(body, "appVersion"), 40),
            LastSeen = DateTime.UtcNow,
        });
        return NoContent();
    }

    /// <summary>Removes this device (only if it belongs to the signed-in user).</summary>
    [HttpPost("Unregister")]
    public ActionResult Unregister([FromBody] JsonElement body)
    {
        var uid = UserId();
        var deviceId = Str(body, "deviceId");
        if (uid != Guid.Empty && !string.IsNullOrWhiteSpace(deviceId))
        {
            _store.Unregister(uid.ToString("N"), deviceId.Trim());
        }

        return NoContent();
    }

    /// <summary>Sends a test message to the signed-in user's devices.</summary>
    [HttpPost("Test")]
    public async Task<ActionResult> Test(CancellationToken ct)
    {
        var uid = UserId();
        if (uid == Guid.Empty)
        {
            return Unauthorized();
        }

        var sent = await _notifier.SendTestAsync(uid, ct).ConfigureAwait(false);
        return sent > 0 ? NoContent() : NotFound();
    }

    /// <summary>For the settings page: what is set up, and how many devices are registered.</summary>
    [HttpGet("Admin/Status")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult AdminStatus()
    {
        Response.Headers.CacheControl = "no-store";
        var c = Plugin.Instance!.Configuration;
        var account = _store.Account();
        var devices = _store.All();
        var me = UserId().ToString("N");
        string? warning = null;
        if (account?.ProjectId is { Length: > 0 } p && !string.IsNullOrWhiteSpace(c.PushProjectId) && !string.Equals(p, c.PushProjectId, StringComparison.Ordinal))
        {
            warning = $"The service account is for project “{p}”, but google-services.json is for “{c.PushProjectId}”. Both must come from the same Firebase project.";
        }

        return Ok(new
        {
            enabled = c.PushEnabled,
            ready = _notifier.Firebase() is not null,
            projectId = c.PushProjectId,
            hasGoogleServices = !string.IsNullOrWhiteSpace(c.PushAppId),
            serviceAccountEmail = account?.ClientEmail,
            devices = devices.Count,
            users = devices.Select(d => d.UserId).Distinct(StringComparer.OrdinalIgnoreCase).Count(),
            myDevices = devices.Count(d => string.Equals(d.UserId, me, StringComparison.OrdinalIgnoreCase)),
            deviceUsers = devices.GroupBy(d => d.UserId, StringComparer.OrdinalIgnoreCase)
                .Select(g => new { userId = g.Key, count = g.Count() }).ToList(),
            warning,
        });
    }

    /// <summary>
    /// Sends the owner's own message: body { "title": "…", "message": "…", "userIds": ["…"] }
    /// (no userIds = everyone with PHOENIXA set up for notifications).
    /// </summary>
    [HttpPost("Admin/Send")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public async Task<ActionResult> SendMessage([FromBody] JsonElement body, CancellationToken ct)
    {
        var title = (Str(body, "title") ?? string.Empty).Trim();
        var message = (Str(body, "message") ?? string.Empty).Trim();
        if (message.Length == 0)
        {
            return BadRequest(new { message = "Write a message first." });
        }

        if (title.Length == 0)
        {
            title = "PHOENIXA";
        }

        if (title.Length > 80 || message.Length > 500)
        {
            return BadRequest(new { message = "Keep the title under 80 and the message under 500 characters." });
        }

        var only = new List<Guid>();
        if (body.ValueKind == JsonValueKind.Object && body.TryGetProperty("userIds", out var ids) && ids.ValueKind == JsonValueKind.Array)
        {
            foreach (var x in ids.EnumerateArray())
            {
                if (x.ValueKind == JsonValueKind.String && Guid.TryParse(x.GetString(), out var g))
                {
                    only.Add(g);
                }
            }
        }

        if (_notifier.Firebase() is null)
        {
            return BadRequest(new { message = "Push notifications aren't on or set up yet." });
        }

        var (users, ok, failed) = await _notifier.SendMessageAsync(title, message, only, ct).ConfigureAwait(false);
        return Ok(new { users, ok, failed });
    }

    /// <summary>Saves the Firebase app settings from a pasted google-services.json.</summary>
    [HttpPost("Admin/GoogleServices")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveGoogleServices([FromBody] JsonElement body)
    {
        var text = Str(body, "json");
        if (string.IsNullOrWhiteSpace(text))
        {
            return BadRequest(new { message = "Paste the contents of google-services.json." });
        }

        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (!root.TryGetProperty("project_info", out var info) || !root.TryGetProperty("client", out var clients) || clients.ValueKind != JsonValueKind.Array)
            {
                return BadRequest(new { message = "This isn't a google-services.json file (no project_info or client part)." });
            }

            var projectId = Str(info, "project_id");
            var senderId = Str(info, "project_number");
            foreach (var cl in clients.EnumerateArray())
            {
                if (!cl.TryGetProperty("client_info", out var ci) || !ci.TryGetProperty("android_client_info", out var ai)
                    || !string.Equals(Str(ai, "package_name"), PackageName, StringComparison.Ordinal))
                {
                    continue;
                }

                var appId = Str(ci, "mobilesdk_app_id");
                string? apiKey = null;
                if (cl.TryGetProperty("api_key", out var keys) && keys.ValueKind == JsonValueKind.Array && keys.GetArrayLength() > 0)
                {
                    apiKey = Str(keys[0], "current_key");
                }

                if (string.IsNullOrWhiteSpace(projectId) || string.IsNullOrWhiteSpace(senderId) || string.IsNullOrWhiteSpace(appId) || string.IsNullOrWhiteSpace(apiKey))
                {
                    return BadRequest(new { message = "The file is missing the project id, project number, app id or API key." });
                }

                var c = Plugin.Instance!.Configuration;
                c.PushProjectId = projectId;
                c.PushSenderId = senderId;
                c.PushAppId = appId;
                c.PushApiKey = apiKey;
                Plugin.Instance.SaveConfiguration();
                return Ok(new { projectId });
            }

            return BadRequest(new { message = $"This file has no Android app with package name {PackageName}. In Firebase, add an Android app with exactly that package name and download its google-services.json." });
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "That isn't valid JSON. Paste the whole google-services.json file." });
        }
    }

    /// <summary>Saves the Firebase service account (kept secret: only its email is ever shown).</summary>
    [HttpPost("Admin/ServiceAccount")]
    [Authorize(Policy = Policies.RequiresElevation)]
    public ActionResult SaveServiceAccount([FromBody] JsonElement body)
    {
        var text = Str(body, "json");
        if (string.IsNullOrWhiteSpace(text))
        {
            return BadRequest(new { message = "Paste the service account key file." });
        }

        ServiceAccount? account;
        try
        {
            account = JsonSerializer.Deserialize<ServiceAccount>(text);
        }
        catch (JsonException)
        {
            return BadRequest(new { message = "That isn't valid JSON. Paste the whole key file." });
        }

        if (account is null)
        {
            return BadRequest(new { message = "That isn't a service account key." });
        }

        if (FcmSender.Validate(account) is { } problem)
        {
            return BadRequest(new { message = problem });
        }

        try
        {
            _store.SaveAccount(account);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Torvia push: could not save the service account: {Msg}", ex.Message);
            return StatusCode(409, new { message = "The key could not be saved on the server." });
        }

        _fcm.Reset();
        _log.LogInformation("Torvia push: service account saved ({Email})", account.ClientEmail);
        return Ok(new { serviceAccountEmail = account.ClientEmail });
    }

    private Guid UserId()
    {
        Guid.TryParse(User.FindFirstValue("Jellyfin-UserId"), out var id);
        return id;
    }

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var p in e.EnumerateObject())
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p.Value.ValueKind switch
                {
                    JsonValueKind.String => p.Value.GetString(),
                    JsonValueKind.Number => p.Value.GetRawText(),
                    _ => null,
                };
            }
        }

        return null;
    }

    private static string? Cut(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max];
}
