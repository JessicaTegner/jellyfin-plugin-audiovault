using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AudioVault.Core.Sources;

namespace Jellyfin.Plugin.AudioVault.Services;

/// <summary>
/// Says who is playing a stream URL. Added to the URL a client gets from PlaybackInfo, so the client's own request
/// carries the user, while the .strm file (read by probes and the server's transcoder) stays the same for everyone.
/// </summary>
public sealed record PlaybackTicket(Guid UserId, string AccountKey, string EntryId, int? Episode, string? SessionId, DateTimeOffset Expires)
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromHours(6);

    public const string QueryName = "t";

    /// <summary>The generation of the account the ticket was issued for; a removed or replaced account has moved on.</summary>
    public long Generation { get; init; }

    /// <summary>The Jellyfin item played and the version (media source) the URL belongs to, for a blocked play's dialog.</summary>
    public Guid? ItemId { get; init; }

    public Guid? VersionId { get; init; }

    /// <summary>The PlaybackInfo's PlaySessionId, which the player reports with its start and stop of this attempt.</summary>
    public string? PlaySessionId { get; init; }

    /// <summary>
    /// Whether this ticket may download with the user's account as it is now: the same login and the same generation.
    /// Removing the account, or saving it again (even with the same email), voids every ticket issued before.
    /// </summary>
    public bool Authorizes(SourceAccount? current) =>
        current is not null
        && AccountKey.Length > 0
        && string.Equals(current.Key, AccountKey, StringComparison.Ordinal)
        && current.Generation == Generation;

    public string Create(string secret)
    {
        var payload = string.Join(
            '|',
            UserId.ToString("N"),
            Uri.EscapeDataString(AccountKey),
            Uri.EscapeDataString(EntryId),
            Episode?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            Uri.EscapeDataString(SessionId ?? string.Empty),
            Expires.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            Generation.ToString(CultureInfo.InvariantCulture),
            ItemId?.ToString("N") ?? string.Empty,
            VersionId?.ToString("N") ?? string.Empty,
            Uri.EscapeDataString(PlaySessionId ?? string.Empty));
        var encoded = Base64Url(Encoding.UTF8.GetBytes(payload));
        return encoded + "." + Mac(secret, encoded);
    }

    /// <summary>The ticket if it is genuine, unexpired and for this stream; otherwise null.</summary>
    public static PlaybackTicket? Read(string secret, string? token, string entryId, int? episode, DateTimeOffset now)
    {
        if (string.IsNullOrEmpty(token) || string.IsNullOrEmpty(secret) || token.Split('.') is not [var encoded, var mac])
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(Mac(secret, encoded)), Encoding.ASCII.GetBytes(mac)))
        {
            return null;
        }

        string payload;
        try
        {
            payload = Encoding.UTF8.GetString(FromBase64Url(encoded));
        }
        catch (FormatException)
        {
            return null;
        }

        var parts = payload.Split('|');
        // Tickets issued before the PlaySessionId was added have 9 parts.
        if (parts.Length is not (9 or 10)
            || !Guid.TryParseExact(parts[0], "N", out var userId)
            || !long.TryParse(parts[5], NumberStyles.None, CultureInfo.InvariantCulture, out var expires)
            || !long.TryParse(parts[6], NumberStyles.None, CultureInfo.InvariantCulture, out var generation)
            || !TryParseOptional(parts[7], out var itemId)
            || !TryParseOptional(parts[8], out var versionId))
        {
            return null;
        }

        int? ep = parts[3].Length == 0 ? null
            : int.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out var e) ? e : -1;
        var ticket = new PlaybackTicket(
            userId,
            Uri.UnescapeDataString(parts[1]),
            Uri.UnescapeDataString(parts[2]),
            ep,
            parts[4].Length == 0 ? null : Uri.UnescapeDataString(parts[4]),
            DateTimeOffset.FromUnixTimeSeconds(expires))
        {
            Generation = generation,
            ItemId = itemId,
            VersionId = versionId,
            PlaySessionId = parts.Length > 9 && parts[9].Length > 0 ? Uri.UnescapeDataString(parts[9]) : null,
        };
        return ticket.EntryId == entryId && ticket.Episode == episode && now < ticket.Expires ? ticket : null;
    }

    /// <summary>Adds a ticket to a stream URL, replacing one that is already there.</summary>
    public static string AddTo(string url, string token)
    {
        var marker = url.IndexOf("&" + QueryName + "=", StringComparison.Ordinal);
        if (marker >= 0)
        {
            var end = url.IndexOf('&', marker + 1);
            url = end < 0 ? url[..marker] : url[..marker] + url[end..];
        }

        return url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + QueryName + "=" + token;
    }

    /// <summary>
    /// Names the attempt in a stream URL, replacing a name already there. jellyfin-web reports a direct play's
    /// PlaySessionId only when its URL carries one, and the plugin matches the player's start and stop reports by it.
    /// Not trusted for anything; the ticket says who may download.
    /// </summary>
    public static string WithPlaySession(string url, string? playSessionId)
    {
        const string Name = "PlaySessionId=";
        foreach (var separator in new[] { '&', '?' })
        {
            var marker = url.IndexOf(separator + Name, StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var end = url.IndexOf('&', marker + 1);
                url = separator == '?'
                    ? (end < 0 ? url[..marker] : url[..marker] + "?" + url[(end + 1)..])
                    : (end < 0 ? url[..marker] : url[..marker] + url[end..]);
            }
        }

        return string.IsNullOrEmpty(playSessionId)
            ? url
            : url + (url.Contains('?', StringComparison.Ordinal) ? "&" : "?") + Name + Uri.EscapeDataString(playSessionId);
    }

    private static bool TryParseOptional(string value, out Guid? id)
    {
        id = null;
        if (value.Length == 0)
        {
            return true;
        }

        if (!Guid.TryParseExact(value, "N", out var parsed))
        {
            return false;
        }

        id = parsed;
        return true;
    }

    // Separate from the URL signature's key so neither MAC can stand in for the other.
    private static string Mac(string secret, string encoded) =>
        Base64Url(HMACSHA256.HashData(Encoding.UTF8.GetBytes("ticket:" + secret), Encoding.ASCII.GetBytes(encoded)));

    private static string Base64Url(ReadOnlySpan<byte> bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var s = value.Replace('-', '+').Replace('_', '/');
        return Convert.FromBase64String(s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '='));
    }
}

/// <summary>Who fetched a stream URL without a ticket.</summary>
public static class StreamRequesters
{
    /// <summary>
    /// Jellyfin's own fetches of the .strm URL: ffmpeg/ffprobe (Lavf) for probes and transcoding, and the server's
    /// HTTP client when it relays a remote stream. Clients get a ticketed URL from PlaybackInfo instead.
    /// </summary>
    public static bool IsServer(string? userAgent) =>
        userAgent is not null
        && (userAgent.StartsWith("Lavf", StringComparison.OrdinalIgnoreCase)
            || userAgent.StartsWith("Jellyfin-Server", StringComparison.OrdinalIgnoreCase));
}
