using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Push;

/// <summary>One phone (or other device) that wants push notifications.</summary>
public sealed class PushDevice
{
    /// <summary>Gets or sets the Jellyfin user id (32 hex letters).</summary>
    public string UserId { get; set; } = string.Empty;

    /// <summary>Gets or sets the Jellyfin device id.</summary>
    public string DeviceId { get; set; } = string.Empty;

    /// <summary>Gets or sets the FCM token.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Gets or sets the device name.</summary>
    public string? DeviceName { get; set; }

    /// <summary>Gets or sets the app version.</summary>
    public string? AppVersion { get; set; }

    /// <summary>Gets or sets when the device last registered (UTC).</summary>
    public DateTime LastSeen { get; set; }
}

/// <summary>The Firebase service account (secret). Only the fields that are used.</summary>
public sealed class ServiceAccount
{
    /// <summary>Gets or sets the account email.</summary>
    [JsonPropertyName("client_email")]
    public string ClientEmail { get; set; } = string.Empty;

    /// <summary>Gets or sets the private key (PEM).</summary>
    [JsonPropertyName("private_key")]
    public string PrivateKey { get; set; } = string.Empty;

    /// <summary>Gets or sets the Firebase project id.</summary>
    [JsonPropertyName("project_id")]
    public string? ProjectId { get; set; }

    /// <summary>Gets or sets the token address.</summary>
    [JsonPropertyName("token_uri")]
    public string? TokenUri { get; set; }
}

/// <summary>
/// Keeps the registered devices and the service account in files in Jellyfin's data folder
/// (not in the plugin settings, so the secret key is never sent to the settings page).
/// </summary>
public sealed class PushStore
{
    private readonly ILogger<PushStore> _log;
    private readonly object _lock = new();
    private List<PushDevice>? _devices;
    private ServiceAccount? _account;
    private bool _accountLoaded;

    /// <summary>Initializes a new instance of the <see cref="PushStore"/> class.</summary>
    public PushStore(ILogger<PushStore> log) => _log = log;

    private static string Folder => Path.Combine(Plugin.Instance!.DataPath, "torvia-push");

    private static string DevicesFile => Path.Combine(Folder, "devices.json");

    private static string AccountFile => Path.Combine(Folder, "service-account.json");

    /// <summary>A copy of all registered devices.</summary>
    public List<PushDevice> All()
    {
        lock (_lock)
        {
            return Load().Select(Copy).ToList();
        }
    }

    /// <summary>
    /// Adds or replaces a device. One entry per device id, and one entry per token, so a phone
    /// never gets the same push twice.
    /// </summary>
    public void Register(PushDevice d)
    {
        lock (_lock)
        {
            var list = Load();
            list.RemoveAll(x => string.Equals(x.DeviceId, d.DeviceId, StringComparison.Ordinal)
                || string.Equals(x.Token, d.Token, StringComparison.Ordinal));
            list.Add(d);
            Save(list);
        }
    }

    /// <summary>Removes a device, but only if it belongs to this user.</summary>
    public bool Unregister(string userId, string deviceId)
    {
        lock (_lock)
        {
            var list = Load();
            var n = list.RemoveAll(x => string.Equals(x.DeviceId, deviceId, StringComparison.Ordinal)
                && string.Equals(x.UserId, userId, StringComparison.OrdinalIgnoreCase));
            if (n > 0)
            {
                Save(list);
            }

            return n > 0;
        }
    }

    /// <summary>Removes devices by token (FCM said the token is no longer valid).</summary>
    public int RemoveTokens(IEnumerable<string> tokens)
    {
        var set = new HashSet<string>(tokens, StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            var list = Load();
            var n = list.RemoveAll(x => set.Contains(x.Token));
            if (n > 0)
            {
                Save(list);
            }

            return n;
        }
    }

    /// <summary>Removes devices by device id (signed out of Jellyfin).</summary>
    public int RemoveDevices(IEnumerable<string> deviceIds)
    {
        var set = new HashSet<string>(deviceIds, StringComparer.Ordinal);
        if (set.Count == 0)
        {
            return 0;
        }

        lock (_lock)
        {
            var list = Load();
            var n = list.RemoveAll(x => set.Contains(x.DeviceId));
            if (n > 0)
            {
                Save(list);
            }

            return n;
        }
    }

    /// <summary>The saved service account, or null.</summary>
    public ServiceAccount? Account()
    {
        lock (_lock)
        {
            if (!_accountLoaded)
            {
                _accountLoaded = true;
                try
                {
                    if (File.Exists(AccountFile))
                    {
                        _account = JsonSerializer.Deserialize<ServiceAccount>(File.ReadAllText(AccountFile));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
                {
                    _log.LogWarning("Torvia push: could not read the saved service account: {Msg}", ex.Message);
                }
            }

            return _account;
        }
    }

    /// <summary>Saves the service account (only the needed fields), readable by this server only.</summary>
    public void SaveAccount(ServiceAccount account)
    {
        lock (_lock)
        {
            Directory.CreateDirectory(Folder);
            var tmp = AccountFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(account));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(tmp, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            }

            File.Move(tmp, AccountFile, true);
            _account = account;
            _accountLoaded = true;
        }
    }

    private static PushDevice Copy(PushDevice d) => new()
    {
        UserId = d.UserId,
        DeviceId = d.DeviceId,
        Token = d.Token,
        DeviceName = d.DeviceName,
        AppVersion = d.AppVersion,
        LastSeen = d.LastSeen,
    };

    private List<PushDevice> Load()
    {
        if (_devices != null)
        {
            return _devices;
        }

        try
        {
            _devices = File.Exists(DevicesFile)
                ? JsonSerializer.Deserialize<List<PushDevice>>(File.ReadAllText(DevicesFile)) ?? new()
                : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _log.LogWarning("Torvia push: could not read the device list: {Msg}", ex.Message);
            _devices = new();
        }

        return _devices;
    }

    private void Save(List<PushDevice> list)
    {
        try
        {
            Directory.CreateDirectory(Folder);
            var tmp = DevicesFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(list));
            File.Move(tmp, DevicesFile, true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log.LogWarning("Torvia push: could not save the device list: {Msg}", ex.Message);
        }
    }
}
