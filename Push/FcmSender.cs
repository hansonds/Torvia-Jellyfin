using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MediaBrowser.Common.Net;
using Microsoft.Extensions.Logging;

namespace PhoenicxTorBox.Push;

/// <summary>What happened to one message.</summary>
public enum SendResult
{
    /// <summary>Delivered to FCM.</summary>
    Ok,

    /// <summary>FCM says the token is no longer valid: remove the device.</summary>
    BadToken,

    /// <summary>Not sent (FCM busy, network, or a setup problem).</summary>
    Failed,
}

/// <summary>
/// Sends Firebase Cloud Messaging messages (HTTP v1 API), signing in with the service account.
/// Uses Jellyfin's HTTP client, so the server's proxy settings apply.
/// </summary>
public sealed class FcmSender
{
    private const string Scope = "https://www.googleapis.com/auth/firebase.messaging";
    private const string DefaultTokenUri = "https://oauth2.googleapis.com/token";

    private readonly IHttpClientFactory _http;
    private readonly PushStore _store;
    private readonly ILogger<FcmSender> _log;
    private readonly SemaphoreSlim _tokenLock = new(1, 1);
    private string? _accessToken;
    private string? _accessTokenFor;
    private DateTime _accessTokenUntil;

    /// <summary>Initializes a new instance of the <see cref="FcmSender"/> class.</summary>
    public FcmSender(IHttpClientFactory http, PushStore store, ILogger<FcmSender> log)
    {
        _http = http;
        _store = store;
        _log = log;
    }

    /// <summary>
    /// Checks a pasted service account: it must have an email and a private key that can be read.
    /// Returns an error text, or null when it is fine.
    /// </summary>
    public static string? Validate(ServiceAccount a)
    {
        if (string.IsNullOrWhiteSpace(a.ClientEmail) || string.IsNullOrWhiteSpace(a.PrivateKey))
        {
            return "This isn't a service account key: it has no client_email or private_key. In Firebase: Project settings → Service accounts → Generate new private key.";
        }

        try
        {
            using var rsa = RSA.Create();
            rsa.ImportFromPem(a.PrivateKey);
            return null;
        }
        catch (Exception ex) when (ex is ArgumentException or CryptographicException)
        {
            return "The private key in this file can't be read. Download a new key from Firebase and paste the whole file.";
        }
    }

    /// <summary>Forgets the cached sign-in (after the service account was replaced).</summary>
    public void Reset()
    {
        _accessToken = null;
        _accessTokenFor = null;
    }

    /// <summary>Sends one data message to one device.</summary>
    public async Task<SendResult> SendAsync(string projectId, string token, IReadOnlyDictionary<string, string> data, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new
        {
            message = new
            {
                token,
                android = new { priority = "HIGH", ttl = "86400s" },
                data,
            },
        });

        for (var attempt = 0; attempt < 2; attempt++)
        {
            string access;
            try
            {
                access = await AccessTokenAsync(ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _log.LogWarning("Torvia push: could not sign in to Firebase: {Msg}", ex.Message);
                return SendResult.Failed;
            }

            HttpResponseMessage res;
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://fcm.googleapis.com/v1/projects/{Uri.EscapeDataString(projectId)}/messages:send")
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", access);
                res = await Client().SendAsync(req, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpRequestException || (ex is TaskCanceledException && !ct.IsCancellationRequested))
            {
                if (attempt == 0)
                {
                    await Task.Delay(TimeSpan.FromSeconds(3), ct).ConfigureAwait(false);
                    continue;
                }

                _log.LogWarning("Torvia push: Firebase can't be reached: {Msg}", ex.Message);
                return SendResult.Failed;
            }

            using (res)
            {
                if (res.IsSuccessStatusCode)
                {
                    return SendResult.Ok;
                }

                var code = (int)res.StatusCode;
                var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                if (res.StatusCode == HttpStatusCode.NotFound || text.Contains("UNREGISTERED", StringComparison.Ordinal)
                    || (text.Contains("INVALID_ARGUMENT", StringComparison.Ordinal) && text.Contains("registration token", StringComparison.OrdinalIgnoreCase)))
                {
                    return SendResult.BadToken;
                }

                if (code is 401 or 403)
                {
                    // Signed in with an old or wrong key: sign in again next time.
                    Reset();
                    _log.LogWarning("Torvia push: Firebase refused the service account ({Code}). Check that the service account belongs to the same Firebase project.", code);
                    return SendResult.Failed;
                }

                if ((code == 429 || code >= 500) && attempt == 0)
                {
                    var wait = res.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(3);
                    await Task.Delay(wait > TimeSpan.FromSeconds(10) ? TimeSpan.FromSeconds(10) : wait, ct).ConfigureAwait(false);
                    continue;
                }

                _log.LogWarning("Torvia push: Firebase answered {Code}: {Msg}", code, Short(text));
                return SendResult.Failed;
            }
        }

        return SendResult.Failed;
    }

    private HttpClient Client() => _http.CreateClient(NamedClient.Default);

    private static string Short(string s)
    {
        try
        {
            using var doc = JsonDocument.Parse(s);
            if (doc.RootElement.TryGetProperty("error", out var e) && e.TryGetProperty("message", out var m))
            {
                return m.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Not JSON.
        }

        return s.Length > 200 ? s[..200] : s;
    }

    /// <summary>A Google access token for Firebase messaging, kept until 5 minutes before it runs out.</summary>
    private async Task<string> AccessTokenAsync(CancellationToken ct)
    {
        var account = _store.Account() ?? throw new InvalidOperationException("No service account saved.");
        await _tokenLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_accessToken != null && _accessTokenFor == account.ClientEmail && DateTime.UtcNow < _accessTokenUntil)
            {
                return _accessToken;
            }

            var tokenUri = string.IsNullOrWhiteSpace(account.TokenUri) ? DefaultTokenUri : account.TokenUri;
            var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            var header = B64(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" }));
            var claims = B64(JsonSerializer.SerializeToUtf8Bytes(new
            {
                iss = account.ClientEmail,
                scope = Scope,
                aud = tokenUri,
                iat = now,
                exp = now + 3600,
            }));
            var unsigned = header + "." + claims;
            byte[] signature;
            using (var rsa = RSA.Create())
            {
                rsa.ImportFromPem(account.PrivateKey);
                signature = rsa.SignData(Encoding.ASCII.GetBytes(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
            }

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:jwt-bearer",
                ["assertion"] = unsigned + "." + B64(signature),
            });
            using var res = await Client().PostAsync(tokenUri, form, ct).ConfigureAwait(false);
            var text = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            if (!res.IsSuccessStatusCode)
            {
                throw new InvalidOperationException($"Google answered {(int)res.StatusCode}: {Short(text)}");
            }

            using var doc = JsonDocument.Parse(text);
            var token = doc.RootElement.GetProperty("access_token").GetString()
                ?? throw new InvalidOperationException("Google sent no access token.");
            var seconds = doc.RootElement.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var s) ? s : 3600;
            _accessToken = token;
            _accessTokenFor = account.ClientEmail;
            _accessTokenUntil = DateTime.UtcNow.AddSeconds(Math.Max(60, seconds - 300));
            return token;
        }
        finally
        {
            _tokenLock.Release();
        }
    }

    private static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
