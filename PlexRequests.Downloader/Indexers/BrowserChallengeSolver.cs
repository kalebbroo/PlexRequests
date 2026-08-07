using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Solves a Cloudflare interstitial by driving a real headless browser over the DevTools protocol, then
/// hands back the cookie so ordinary HTTP can be used from then on.
///
/// Why a browser at all: the interstitial is a JavaScript proof-of-work plus fingerprint check. No
/// combination of headers, User-Agent spoofing or TLS tuning satisfies it, because the server is waiting on
/// the result of code it expects the client to run. Executing that code needs a browser engine — which is
/// precisely what FlareSolverr is, and why Jackett installs end up running one. In-process means one less
/// service to deploy and keep alive.
///
/// Why the weight is acceptable: the browser runs ONCE per site per clearance lifetime and produces a
/// `cf_clearance` cookie; every search afterwards is a plain HTTP GET carrying it. The expensive path is
/// rare, the hot path unchanged.
///
/// Why DevTools rather than something simpler: `cf_clearance` is HttpOnly, so `document.cookie` cannot see
/// it, and Chromium's on-disk jar is SQLite with platform-dependent encryption. `Network.getAllCookies` is
/// the only route that reliably yields the value — a first version of this class read a JSON cookie file
/// that Chromium does not write, and would have silently never produced a clearance.
///
/// Cloudflare binds the cookie to the exact User-Agent AND the client IP that earned it, so both are
/// returned together and must be replayed together. The browser runs inside this container, sharing the
/// tunnel egress with the HTTP client, which is what makes the IP half hold.
/// </summary>
public class BrowserChallengeSolver(
    IOptions<IndexerOptions> options,
    ILogger<BrowserChallengeSolver> logger) : IChallengeSolver
{
    private readonly IndexerOptions _opts = options.Value;

    /// <summary>One solve at a time. Each costs a browser process and tens of seconds; running several
    /// concurrently multiplies the cost without shortening the wall clock they all wait on.</summary>
    private readonly SemaphoreSlim _gate = new(1, 1);

    public bool IsAvailable => ResolveBrowserPath() is not null;

    public async Task<Clearance?> SolveAsync(IndexerConfigDto indexer, string url, CancellationToken ct)
    {
        var browser = ResolveBrowserPath();
        if (browser is null)
        {
            logger.LogDebug("No browser available to solve a challenge for {Indexer}", indexer.Name);
            return null;
        }

        await _gate.WaitAsync(ct);
        try { return await SolveCoreAsync(browser, indexer, url, ct); }
        finally { _gate.Release(); }
    }

    private async Task<Clearance?> SolveCoreAsync(string browser, IndexerConfigDto indexer, string url, CancellationToken ct)
    {
        // A throwaway profile per solve: Cloudflare keys partly off browser state, and a profile reused
        // between attempts carries forward whatever got us challenged in the first place.
        var profile = Path.Combine(Path.GetTempPath(), $"cfsolve-{Guid.NewGuid():N}");
        Directory.CreateDirectory(profile);

        var timeout = TimeSpan.FromSeconds(Math.Clamp(_opts.ChallengeSolveTimeoutSeconds, 20, 180));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        Process? proc = null;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = browser,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            foreach (var a in new[]
            {
                "--headless=new", "--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage",
                "--remote-debugging-port=0",          // port 0 => the browser picks one and prints it
                $"--user-data-dir={profile}",
                "about:blank"
            }) psi.ArgumentList.Add(a);

            proc = Process.Start(psi);
            if (proc is null) return null;

            var wsUrl = await ReadDevToolsUrlAsync(proc, cts.Token);
            if (wsUrl is null)
            {
                logger.LogWarning("Browser did not report a DevTools endpoint for {Indexer}", indexer.Name);
                return null;
            }

            using var ws = new ClientWebSocket();
            await ws.ConnectAsync(new Uri(wsUrl), cts.Token);

            var id = 0;
            await SendAsync(ws, ++id, "Target.setDiscoverTargets", new { discover = true }, cts.Token);

            // Attach to a page target so navigation and cookie reads share one browsing context.
            var target = await CallAsync(ws, ++id, "Target.createTarget", new { url = "about:blank" }, cts.Token);
            var targetId = target?.GetProperty("targetId").GetString();
            var attached = await CallAsync(ws, ++id, "Target.attachToTarget", new { targetId, flatten = true }, cts.Token);
            var session = attached?.GetProperty("sessionId").GetString();
            if (session is null) return null;

            await SendAsync(ws, ++id, "Page.enable", new { }, cts.Token, session);
            await SendAsync(ws, ++id, "Network.enable", new { }, cts.Token, session);
            await SendAsync(ws, ++id, "Page.navigate", new { url }, cts.Token, session);

            // Let the interstitial run. It is a deliberate delay on Cloudflare's side, so polling faster
            // does not help; this simply has to outlast it.
            var settle = TimeSpan.FromSeconds(Math.Clamp(_opts.ChallengeSettleSeconds, 5, 60));
            try { await Task.Delay(settle, cts.Token); }
            catch (OperationCanceledException) { return null; }

            var cookies = await CallAsync(ws, ++id, "Network.getAllCookies", new { }, cts.Token, session);
            var clearance = FindClearance(cookies);
            if (clearance is null)
            {
                logger.LogWarning("Solve for {Indexer} produced no cf_clearance cookie — the challenge was not satisfied",
                    indexer.Name);
                return null;
            }

            var uaResult = await CallAsync(ws, ++id, "Runtime.evaluate",
                new { expression = "navigator.userAgent", returnByValue = true }, cts.Token, session);
            var ua = uaResult?.GetProperty("result").GetProperty("value").GetString() ?? DefaultUserAgent;

            logger.LogInformation("Earned a Cloudflare clearance for {Indexer}", indexer.Name);
            return new Clearance(clearance, ua);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            logger.LogWarning("Challenge solve for {Indexer} timed out after {Seconds}s", indexer.Name, timeout.TotalSeconds);
            return null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Challenge solve for {Indexer} failed", indexer.Name);
            return null;
        }
        finally
        {
            if (proc is not null) { try { if (!proc.HasExited) proc.Kill(entireProcessTree: true); } catch { } proc.Dispose(); }
            try { Directory.Delete(profile, recursive: true); } catch { }
        }
    }

    /// <summary>Chromium prints "DevTools listening on ws://..." to stderr once it is ready.</summary>
    private static async Task<string?> ReadDevToolsUrlAsync(Process proc, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var line = await proc.StandardError.ReadLineAsync(ct);
            if (line is null) return null;
            var i = line.IndexOf("ws://", StringComparison.Ordinal);
            if (i >= 0) return line[i..].Trim();
        }
        return null;
    }

    private static string? FindClearance(JsonElement? cookiesResult)
    {
        if (cookiesResult is not { } r || !r.TryGetProperty("cookies", out var arr)) return null;
        foreach (var c in arr.EnumerateArray())
        {
            if (c.TryGetProperty("name", out var n) && n.GetString() == "cf_clearance"
                && c.TryGetProperty("value", out var v) && v.GetString() is { Length: > 0 } val)
                return $"cf_clearance={val}";
        }
        return null;
    }

    // ---- Minimal CDP plumbing ----------------------------------------------------------------------

    private static async Task SendAsync(ClientWebSocket ws, int id, string method, object @params,
        CancellationToken ct, string? sessionId = null)
    {
        var payload = sessionId is null
            ? JsonSerializer.Serialize(new { id, method, @params })
            : JsonSerializer.Serialize(new { id, method, @params, sessionId });
        await ws.SendAsync(Encoding.UTF8.GetBytes(payload), WebSocketMessageType.Text, true, ct);
    }

    /// <summary>Send and wait for the reply with the matching id, ignoring the event traffic in between.</summary>
    private static async Task<JsonElement?> CallAsync(ClientWebSocket ws, int id, string method, object @params,
        CancellationToken ct, string? sessionId = null)
    {
        await SendAsync(ws, id, method, @params, ct, sessionId);

        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();
        while (!ct.IsCancellationRequested)
        {
            sb.Clear();
            WebSocketReceiveResult res;
            do
            {
                res = await ws.ReceiveAsync(buffer, ct);
                if (res.MessageType == WebSocketMessageType.Close) return null;
                sb.Append(Encoding.UTF8.GetString(buffer, 0, res.Count));
            } while (!res.EndOfMessage);

            using var doc = JsonDocument.Parse(sb.ToString());
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var rid) && rid.GetInt32() == id)
                return root.TryGetProperty("result", out var result) ? result.Clone() : null;
        }
        return null;
    }

    private string? ResolveBrowserPath()
    {
        if (!string.IsNullOrWhiteSpace(_opts.BrowserPath))
            return File.Exists(_opts.BrowserPath) ? _opts.BrowserPath : null;

        foreach (var candidate in new[]
        {
            "/usr/bin/chromium", "/usr/bin/chromium-browser", "/usr/bin/google-chrome",
            "/usr/lib/chromium/chromium"
        })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
}
