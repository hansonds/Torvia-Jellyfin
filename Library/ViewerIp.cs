using System.Net;
using System.Net.Sockets;
using Microsoft.AspNetCore.Http;

namespace PhoenicxTorBox.Library;

/// <summary>
/// The public internet address of the viewer, so TorBox can send them to its closest server.
/// Behind Cloudflare or a reverse proxy the real address is in a header; local and private
/// addresses (the server itself, home Wi-Fi) are not sent, and TorBox then decides by itself.
/// </summary>
public static class ViewerIp
{
    /// <summary>The viewer's public IP, or null when unknown, private, or the setting is off.</summary>
    public static string? Of(HttpContext? http)
    {
        if (http is null || Plugin.Instance?.Configuration.NearestServerPerViewer != true)
        {
            return null;
        }

        var h = http.Request.Headers;
        var candidates = new[]
        {
            h["CF-Connecting-IP"].ToString(),
            h["X-Forwarded-For"].ToString().Split(',')[0],
            h["X-Real-IP"].ToString(),
            http.Connection.RemoteIpAddress?.ToString() ?? string.Empty,
        };

        foreach (var c in candidates)
        {
            if (IPAddress.TryParse(c.Trim(), out var ip) && IsPublic(ip))
            {
                return (ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip).ToString();
            }
        }

        return null;
    }

    private static bool IsPublic(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6)
        {
            ip = ip.MapToIPv4();
        }

        if (IPAddress.IsLoopback(ip))
        {
            return false;
        }

        if (ip.AddressFamily == AddressFamily.InterNetwork)
        {
            var b = ip.GetAddressBytes();
            return !(b[0] == 10
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0);
        }

        return !(ip.IsIPv6LinkLocal || ip.IsIPv6SiteLocal || ip.IsIPv6UniqueLocal || ip.Equals(IPAddress.IPv6None));
    }
}
