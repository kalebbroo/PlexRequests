using System.Net;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>Why an indexer refused to answer. Distinct from "answered, found nothing".</summary>
public sealed class IndexerBlockedException(IndexerBlockReason reason, string message) : Exception(message)
{
    public IndexerBlockReason Reason { get; } = reason;
}

/// <summary>
/// The single seam every scraped indexer fetches through.
///
/// It exists because scrapers were each doing their own <c>GetStringAsync</c> inside a try/catch that
/// logged a warning and carried on. A 403 therefore reached the client as "search succeeded, zero
/// results" — indistinguishable from a quiet night. 1337x and ext.to ran 13 searches each, returned
/// nothing every time, and still showed a clean bill of health in the admin panel. Blocked is not empty,
/// and only a shared choke point can enforce that consistently.
///
/// It is also the extension point: per-indexer credentials (a browser-harvested clearance cookie) go on
/// here, and the headless-browser challenge solver plugs in behind the same call so no provider has to
/// learn about any of it.
/// </summary>
public interface IIndexerFetch
{
    /// <summary>
    /// GET a page for this indexer through the provider's own <see cref="HttpClient"/> (each has its own
    /// base address, timeout and resilience policy). Throws <see cref="IndexerBlockedException"/> when the
    /// site refuses — it never hands back a challenge page as though it were content, which is precisely
    /// how a wall of 403s came to be recorded as thirteen successful, empty searches.
    /// </summary>
    Task<string> GetStringAsync(HttpClient http, IndexerConfigDto indexer, string url, CancellationToken ct);
}

public class IndexerFetch(
    IChallengeSolver solver,
    PlexRequests.Downloader.Api.IPlexRequestsApiClient api,
    ILogger<IndexerFetch> logger) : IIndexerFetch
{
    /// <summary>
    /// Sites we have already tried and failed to solve this process-lifetime. Without this, every search
    /// against a site whose challenge cannot be beaten would launch a browser and burn a minute — turning
    /// one blocked indexer into a system-wide slowdown.
    /// </summary>
    private readonly HashSet<string> _unsolvable = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cloudflare's interstitial is a 403/503 carrying these markers. Distinguishing the
    /// *challenge* (solvable — a browser can earn a clearance cookie) from a hard IP ban (error 1020,
    /// nothing to be done) decides whether escalating to a browser is worth the cost.</summary>
    public static IndexerBlockReason? DetectBlock(HttpStatusCode status, IReadOnlyDictionary<string, string> headers, string? body)
    {
        var mitigated = headers.TryGetValue("cf-mitigated", out var m) ? m : null;
        var server = headers.TryGetValue("server", out var s) ? s : null;
        var isCloudflare = string.Equals(server, "cloudflare", StringComparison.OrdinalIgnoreCase) || mitigated is not null;

        var succeeded = (int)status is >= 200 and < 300;

        if (body is not null)
        {
            // Only STRUCTURAL markers are trusted on their own. An earlier version also matched the bare
            // phrase "Just a moment" anywhere in the body, which flagged a healthy 200 page listing a
            // torrent called "Just a Moment (2019) 1080p" as a Cloudflare block — taking a working indexer
            // offline because of a film title. These three strings cannot occur in a release name.
            if (body.Contains("error code: 1020", StringComparison.OrdinalIgnoreCase))
                return IndexerBlockReason.IpBanned;

            if (body.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase)
                || body.Contains("cdn-cgi/challenge", StringComparison.OrdinalIgnoreCase))
                return IndexerBlockReason.CloudflareChallenge;

            // Softer signals are only believed when the request did NOT succeed, so real content is never
            // second-guessed on the strength of a phrase.
            if (!succeeded)
            {
                if (isCloudflare && body.Contains("Access denied", StringComparison.OrdinalIgnoreCase))
                    return IndexerBlockReason.IpBanned;
                if (TitleSaysJustAMoment(body))
                    return IndexerBlockReason.CloudflareChallenge;
            }
        }

        if (succeeded) return null;   // answered normally; nothing below applies

        if (string.Equals(mitigated, "challenge", StringComparison.OrdinalIgnoreCase))
            return IndexerBlockReason.CloudflareChallenge;

        return status switch
        {
            HttpStatusCode.TooManyRequests => IndexerBlockReason.RateLimited,
            HttpStatusCode.Forbidden when isCloudflare => IndexerBlockReason.CloudflareChallenge,
            HttpStatusCode.Forbidden => IndexerBlockReason.Forbidden,
            HttpStatusCode.ServiceUnavailable when isCloudflare => IndexerBlockReason.CloudflareChallenge,
            _ => null
        };
    }

    /// <summary>
    /// Fetch through the provider's own <see cref="HttpClient"/>, applying this indexer's stored
    /// credentials. The clearance cookie is bound by Cloudflare to BOTH the client IP and the exact
    /// User-Agent that earned it, so the two are stored and sent together — sending the cookie with a
    /// different UA is worse than sending no cookie at all, because it looks like theft.
    /// </summary>
    private static string AbsoluteUrl(HttpClient http, string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var abs) ? abs.ToString()
        : http.BaseAddress is not null ? new Uri(http.BaseAddress, url).ToString()
        : url;

    private async Task SafePersistAsync(IndexerConfigDto indexer, Clearance clearance, CancellationToken ct)
    {
        // Best-effort: an unsaved clearance still works for this process, it just has to be re-earned later.
        try { await api.SaveIndexerClearanceAsync(indexer.Id, clearance.CookieHeader, clearance.UserAgent, ct); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not persist the clearance for {Indexer}", indexer.Name); }
    }

    /// <summary>Anchored to the document title so a release name can never trip it.</summary>
    private static bool TitleSaysJustAMoment(string body)
    {
        var i = body.IndexOf("<title", StringComparison.OrdinalIgnoreCase);
        if (i < 0) return false;
        var end = body.IndexOf("</title>", i, StringComparison.OrdinalIgnoreCase);
        var title = end > i ? body[i..end] : body[i..Math.Min(body.Length, i + 200)];
        return title.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
            || title.Contains("Attention Required", StringComparison.OrdinalIgnoreCase);
    }

    public Task<string> GetStringAsync(HttpClient http, IndexerConfigDto indexer, string url, CancellationToken ct) =>
        GetStringAsync(http, indexer, url, ct, allowSolve: true);

    private async Task<string> GetStringAsync(HttpClient http, IndexerConfigDto indexer, string url, CancellationToken ct, bool allowSolve)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);

        if (!string.IsNullOrWhiteSpace(indexer.UserAgent))
        {
            req.Headers.Remove("User-Agent");
            req.Headers.TryAddWithoutValidation("User-Agent", indexer.UserAgent);
        }
        if (!string.IsNullOrWhiteSpace(indexer.ClearanceCookie))
            req.Headers.TryAddWithoutValidation("Cookie", indexer.ClearanceCookie);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct);
        var headers = resp.Headers.Concat(resp.Content.Headers)
            .ToDictionary(h => h.Key, h => string.Join(",", h.Value), StringComparer.OrdinalIgnoreCase);

        string? body = null;
        // Read the body even on a failure status: Cloudflare's markers live in it, and telling a solvable
        // challenge apart from a permanent ban is the difference between retrying usefully and hammering.
        try { body = await resp.Content.ReadAsStringAsync(ct); } catch { /* body is optional evidence */ }

        var block = DetectBlock(resp.StatusCode, headers, body);
        if (block is IndexerBlockReason reason)
        {
            // Escalate to a browser ONLY for a solvable challenge, and only once per site per process. An
            // IP ban is not solvable by any client-side means, so spending a browser launch on it would
            // waste a minute per search and still fail; a rate limit wants patience, not a heavier client.
            if (reason == IndexerBlockReason.CloudflareChallenge && allowSolve && solver.IsAvailable
                && !_unsolvable.Contains(indexer.Name))
            {
                var clearance = await solver.SolveAsync(indexer, AbsoluteUrl(http, url), ct);
                if (clearance is not null)
                {
                    // Persist so the clearance survives a restart and is shared by every later search,
                    // rather than being re-earned per process.
                    await SafePersistAsync(indexer, clearance, ct);
                    indexer.ClearanceCookie = clearance.CookieHeader;
                    indexer.UserAgent = clearance.UserAgent;

                    // One retry with the clearance. allowSolve:false makes this terminal — a second failure
                    // means the cookie did not help, and looping would be indistinguishable from a hang.
                    return await GetStringAsync(http, indexer, url, ct, allowSolve: false);
                }
                _unsolvable.Add(indexer.Name);
            }

            logger.LogWarning("{Indexer} is blocked ({Reason}) fetching {Url}", indexer.Name, reason, url);
            throw new IndexerBlockedException(reason,
                $"{indexer.Name} refused the request ({reason}, HTTP {(int)resp.StatusCode})");
        }

        resp.EnsureSuccessStatusCode();
        return body ?? string.Empty;
    }
}
