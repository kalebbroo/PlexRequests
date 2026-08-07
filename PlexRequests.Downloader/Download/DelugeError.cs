using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Download;

/// <summary>What an error string coming back from Deluge's JSON-RPC actually means to us.</summary>
public enum DelugeErrorKind
{
    /// <summary>Nothing recognised — surface it as a genuine failure.</summary>
    Unknown = 0,
    /// <summary>The session is gone; log in again and retry once.</summary>
    NotAuthenticated = 1,
    /// <summary>Deluge already has this torrent. The desired end state holds, so this is a success.</summary>
    AlreadyInSession = 2,
    /// <summary>The torrent id isn't known to Deluge (removed by hand, or state lost).</summary>
    UnknownTorrent = 3,
}

/// <summary>
/// Classifies Deluge RPC error messages.
///
/// Pulled out of the client as a pure function for one reason: the original classification was
/// <c>msg.Contains("auth") || msg.Contains("session")</c>, and Deluge answers a duplicate add with
/// "Torrent already in session (&lt;hash&gt;)". That substring match turned an ordinary duplicate into an
/// expired-login, so the client re-authenticated, retried the identical add, failed identically, and the
/// job sat at 0% with the torrent already downloading in front of it. A category error of that shape is
/// invisible in review and obvious in a test, so it lives here where it can be pinned.
/// </summary>
public static partial class DelugeError
{
    public static DelugeErrorKind Classify(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return DelugeErrorKind.Unknown;

        // Ordered most-specific first. "already in session" must be tested BEFORE anything matching on the
        // bare word "session", which is the trap this whole class exists to document.
        if (AlreadyInSession().IsMatch(message)) return DelugeErrorKind.AlreadyInSession;
        if (UnknownTorrent().IsMatch(message)) return DelugeErrorKind.UnknownTorrent;
        if (NotAuthenticated().IsMatch(message)) return DelugeErrorKind.NotAuthenticated;
        return DelugeErrorKind.Unknown;
    }

    /// <summary>
    /// The torrent id out of a message that carries one, e.g. "Torrent already in session (0123…)".
    /// Deluge's own id is authoritative and is what we join on: for a v2 or hybrid torrent it is NOT the v1
    /// infohash derivable from the magnet, so computing our own would silently drift apart from Deluge's.
    /// Accepts 40-hex (v1) and 64-hex (v2) forms.
    /// </summary>
    public static string? HashIn(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return null;
        var m = HashPattern().Match(message);
        return m.Success ? m.Groups[1].Value.ToLowerInvariant() : null;
    }

    [GeneratedRegex(@"already\s+in\s+session", RegexOptions.IgnoreCase)]
    private static partial Regex AlreadyInSession();

    // Deluge's wording for a login problem. Anchored on real phrases rather than the word "auth" anywhere,
    // so a torrent named "Authority" or a tracker error mentioning authorisation can't trip it.
    [GeneratedRegex(@"not\s+authenticated|invalid\s+session|password\s+does\s+not\s+match|bad\s+login",
        RegexOptions.IgnoreCase)]
    private static partial Regex NotAuthenticated();

    [GeneratedRegex(@"unknown\s+torrent|torrent\s+not\s+found|invalid\s+torrent(?:\s+id)?", RegexOptions.IgnoreCase)]
    private static partial Regex UnknownTorrent();

    [GeneratedRegex(@"\(([0-9a-f]{40}|[0-9a-f]{64})\)", RegexOptions.IgnoreCase)]
    private static partial Regex HashPattern();
}
