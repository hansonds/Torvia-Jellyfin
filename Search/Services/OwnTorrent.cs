using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace TorviaSearch.Services;

/// <summary>Reads the hash and name out of a magnet link or a .torrent file a user supplies.</summary>
public static class OwnTorrent
{
    private static readonly Regex Btih = new(@"xt=urn:btih:([A-Za-z0-9]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Dn = new(@"[?&]dn=([^&]+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>The torrent's hash (40 hex letters, lowercase) from a magnet link, or null if it isn't one.</summary>
    public static string? HashFromMagnet(string? magnet)
    {
        if (string.IsNullOrWhiteSpace(magnet) || !magnet.TrimStart().StartsWith("magnet:", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var m = Btih.Match(magnet);
        if (!m.Success)
        {
            return null;
        }

        var v = m.Groups[1].Value;
        if (v.Length == 40 && v.All(Uri.IsHexDigit))
        {
            return v.ToLowerInvariant();
        }

        // Older magnet links write the same hash in 32 base32 letters.
        return v.Length == 32 ? Base32ToHex(v) : null;
    }

    /// <summary>The name inside a magnet link (its dn= part), if it has one.</summary>
    public static string? NameFromMagnet(string? magnet)
    {
        var m = Dn.Match(magnet ?? string.Empty);
        if (!m.Success)
        {
            return null;
        }

        try
        {
            var name = Uri.UnescapeDataString(m.Groups[1].Value.Replace('+', ' ')).Trim();
            return name.Length > 0 ? name : null;
        }
        catch (UriFormatException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a .torrent file: its hash (SHA-1 of the "info" part) and its name.
    /// Returns null when the file isn't a torrent.
    /// </summary>
    public static (string Hash, string? Name)? ReadTorrentFile(byte[] data)
    {
        try
        {
            var pos = 0;
            if (data.Length < 20 || data[0] != (byte)'d')
            {
                return null;
            }

            pos++;
            while (pos < data.Length && data[pos] != (byte)'e')
            {
                var key = ReadBytes(data, ref pos);
                var start = pos;
                if (Encoding.ASCII.GetString(key) != "info")
                {
                    Skip(data, ref pos, 0);
                    continue;
                }

                if (data[pos] != (byte)'d')
                {
                    return null;
                }

                // Look for the name inside the info part, then take the hash of the whole part.
                string? name = null;
                var p = pos + 1;
                while (p < data.Length && data[p] != (byte)'e')
                {
                    var k = Encoding.ASCII.GetString(ReadBytes(data, ref p));
                    if (k is "name" or "name.utf-8" && p < data.Length && data[p] >= (byte)'0' && data[p] <= (byte)'9')
                    {
                        var value = Encoding.UTF8.GetString(ReadBytes(data, ref p)).Trim();
                        if (value.Length > 0 && (name == null || k == "name.utf-8"))
                        {
                            name = value;
                        }
                    }
                    else
                    {
                        Skip(data, ref p, 0);
                    }
                }

                Skip(data, ref pos, 0);
                var hash = SHA1.HashData(data.AsSpan(start, pos - start));
                return (Convert.ToHexString(hash).ToLowerInvariant(), name);
            }

            return null;
        }
        catch (Exception e) when (e is FormatException or IndexOutOfRangeException or ArgumentException or OverflowException)
        {
            return null;
        }
    }

    // One "length:bytes" string.
    private static byte[] ReadBytes(byte[] d, ref int pos)
    {
        var colon = Array.IndexOf(d, (byte)':', pos);
        if (colon < 0 || colon - pos > 10)
        {
            throw new FormatException();
        }

        var len = int.Parse(Encoding.ASCII.GetString(d, pos, colon - pos), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture);
        if (len < 0 || colon + 1 + len > d.Length)
        {
            throw new FormatException();
        }

        pos = colon + 1 + len;
        return d.AsSpan(colon + 1, len).ToArray();
    }

    // Steps over one value of any kind.
    private static void Skip(byte[] d, ref int pos, int depth)
    {
        if (depth > 64 || pos >= d.Length)
        {
            throw new FormatException();
        }

        var c = d[pos];
        if (c == (byte)'i')
        {
            var end = Array.IndexOf(d, (byte)'e', pos);
            if (end < 0)
            {
                throw new FormatException();
            }

            pos = end + 1;
        }
        else if (c == (byte)'l' || c == (byte)'d')
        {
            pos++;
            while (pos < d.Length && d[pos] != (byte)'e')
            {
                if (c == (byte)'d')
                {
                    ReadBytes(d, ref pos);
                }

                Skip(d, ref pos, depth + 1);
            }

            if (pos >= d.Length)
            {
                throw new FormatException();
            }

            pos++;
        }
        else if (c >= (byte)'0' && c <= (byte)'9')
        {
            ReadBytes(d, ref pos);
        }
        else
        {
            throw new FormatException();
        }
    }

    private static string? Base32ToHex(string s)
    {
        const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
        var bytes = new byte[20];
        int buffer = 0, bits = 0, n = 0;
        foreach (var ch in s.ToUpperInvariant())
        {
            var v = Alphabet.IndexOf(ch, StringComparison.Ordinal);
            if (v < 0)
            {
                return null;
            }

            buffer = (buffer << 5) | v;
            bits += 5;
            if (bits >= 8)
            {
                bits -= 8;
                if (n >= bytes.Length)
                {
                    return null;
                }

                bytes[n++] = (byte)((buffer >> bits) & 0xFF);
                buffer &= (1 << bits) - 1;
            }
        }

        return n == 20 ? Convert.ToHexString(bytes).ToLowerInvariant() : null;
    }
}
