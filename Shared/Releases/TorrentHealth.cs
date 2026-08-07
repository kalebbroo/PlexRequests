using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>What the reconciler observed about one torrent in the download client, this cycle.</summary>
public sealed record TorrentObservation
{
    /// <summary>The client's own state string, lowercased ("downloading", "seeding", "queued", "error", …).</summary>
    public string ClientState { get; init; } = string.Empty;
    public double Progress { get; init; }
    public int Seeds { get; init; }
    public int Peers { get; init; }
    public bool IsFinished { get; init; }
    public string? TrackerStatus { get; init; }
    /// <summary>False when the client no longer has this torrent at all.</summary>
    public bool PresentInClient { get; init; } = true;
    /// <summary>True once metadata has resolved (a magnet starts with none, so size is unknown).</summary>
    public bool HasMetadata { get; init; } = true;
}

/// <summary>What to do with it.</summary>
public enum TorrentVerdict
{
    /// <summary>Healthy or legitimately waiting. Leave it alone.</summary>
    Keep = 0,
    /// <summary>Downloaded; hand it to the importer.</summary>
    Import = 1,
    /// <summary>Give up on this torrent, blocklist the release, and let the job find another.</summary>
    Fail = 2,
    /// <summary>The client doesn't have it any more; the job must re-plan for what it covered.</summary>
    Gone = 3
}

public sealed record TorrentDecision(TorrentVerdict Verdict, string? Reason = null, bool Blocklist = false);

/// <summary>
/// Decides the fate of a single tracked torrent. Pure, so the rules can be pinned by tests — they are
/// exactly the kind of thing that looks obviously right and quietly destroys good downloads.
///
/// The conservative bias is deliberate and load-bearing. With 20 concurrent downloads on one VPN tunnel,
/// most torrents at any instant are legitimately doing nothing: queued behind the concurrency cap, or in a
/// thin swarm with no peers online right now. A naive "no progress and no peers ⇒ dead" rule would
/// blocklist perfectly good releases in bulk and teach the system to reject them forever after. So:
/// queued torrents are never judged, and a stall must persist across a window long enough to outlast a VPN
/// reconnect, which on this deployment is a routine event rather than an incident.
/// </summary>
public static class TorrentHealth
{
    /// <summary>How long a torrent may make no progress before it is considered dead. Generous on purpose:
    /// a VPN reconnect drops every peer, and recovery takes minutes.</summary>
    public static readonly TimeSpan DefaultStallWindow = TimeSpan.FromMinutes(45);

    /// <summary>A magnet with no metadata yet has nothing to download — it's resolving, not stalled. It
    /// gets its own, shorter budget, because a magnet whose metadata never arrives is genuinely dead.</summary>
    public static readonly TimeSpan MetadataWindow = TimeSpan.FromMinutes(20);

    public static TorrentDecision Decide(
        TorrentObservation obs,
        DateTime addedAt,
        DateTime? progressChangedAt,
        DateTime now,
        TimeSpan? stallWindow = null)
    {
        // Vanished from the client. Nothing to wait for; the job must re-plan.
        if (!obs.PresentInClient)
            return new TorrentDecision(TorrentVerdict.Gone, "No longer in the download client");

        // Finished beats every other consideration: a completed torrent is a success even if it is now
        // sitting at 0 peers, erroring on a tracker, or otherwise looking unhealthy.
        if (obs.IsFinished || obs.Progress >= 100)
            return new TorrentDecision(TorrentVerdict.Import);

        var state = obs.ClientState?.ToLowerInvariant() ?? string.Empty;

        // The client's own error state is authoritative and immediate — no window needed.
        if (state == "error")
            return new TorrentDecision(TorrentVerdict.Fail,
                $"The download client reported an error{Detail(obs.TrackerStatus)}", Blocklist: true);

        // Queued/paused torrents are NOT judged. They are not progressing because we told them not to, and
        // failing them would mean blocklisting good releases purely for being behind others in the queue.
        if (state is "queued" or "paused" or "checking" or "allocating" or "moving")
            return new TorrentDecision(TorrentVerdict.Keep);

        var stalled = stallWindow ?? DefaultStallWindow;

        // A magnet that never resolves metadata is dead in a way that no amount of waiting fixes.
        if (!obs.HasMetadata)
            return now - addedAt > MetadataWindow
                ? new TorrentDecision(TorrentVerdict.Fail,
                    $"No torrent metadata after {MetadataWindow.TotalMinutes:0} minutes — the magnet has no reachable peers",
                    Blocklist: true)
                : new TorrentDecision(TorrentVerdict.Keep);

        // Everything else: give up only when it has made no progress for the whole window AND has nobody
        // to talk to. Either alone is normal — a slow swarm still creeps forward, and a fast one can
        // briefly show zero peers between announces.
        var since = progressChangedAt ?? addedAt;
        if (now - since > stalled && obs.Peers == 0 && obs.Seeds == 0)
            return new TorrentDecision(TorrentVerdict.Fail,
                $"No progress or peers for {(now - since).TotalMinutes:0} minutes{Detail(obs.TrackerStatus)}",
                Blocklist: true);

        return new TorrentDecision(TorrentVerdict.Keep);
    }

    private static string Detail(string? trackerStatus) =>
        string.IsNullOrWhiteSpace(trackerStatus) ? string.Empty : $" ({trackerStatus})";
}
