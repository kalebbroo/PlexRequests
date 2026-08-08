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

    /// <summary>
    /// Cached because the check launches a process. Null = not yet probed.
    /// </summary>
    private bool? _available;

    public bool IsAvailable => _available ??= ProbeBrowser();

    /// <summary>
    /// Existence on disk is not enough. Ubuntu ships /usr/bin/chromium-browser as a stub that refuses to
    /// run and tells you to install the snap — so a File.Exists check reported a browser we did not have,
    /// and every solve would have failed while the code believed it was equipped. Actually run it once.
    /// </summary>
    private bool ProbeBrowser()
    {
        var path = ResolveBrowserPath();
        if (path is null) return false;

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = path,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            psi.ArgumentList.Add("--version");

            using var p = Process.Start(psi);
            if (p is null) return false;

            var stdout = p.StandardOutput.ReadToEnd();
            var stderr = p.StandardError.ReadToEnd();
            if (!p.WaitForExit(15_000)) { try { p.Kill(true); } catch { } return false; }

            var output = stdout + stderr;
            // The stub exits non-zero AND says so; a real browser prints "Chromium 126.0..." / "Google Chrome ...".
            var ok = p.ExitCode == 0
                     && !output.Contains("snap", StringComparison.OrdinalIgnoreCase)
                     && (output.Contains("Chrom", StringComparison.OrdinalIgnoreCase));

            if (!ok)
                logger.LogWarning("Browser at {Path} is not usable ({Output}); challenge solving is disabled",
                    path, output.Trim().Split('\n').FirstOrDefault());
            else
                logger.LogInformation("Challenge solver ready using {Path} ({Version})", path, output.Trim());

            return ok;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not probe the browser at {Path}; challenge solving is disabled", path);
            return false;
        }
    }

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
            // The first version launched a bare headless Chrome and Cloudflare rejected every solve — which
            // is the expected outcome, not bad luck. Headless Chrome announces itself: --enable-automation
            // is on by default and sets navigator.webdriver, the AutomationControlled blink feature is
            // directly detectable, and the default window has no realistic size or language. None of this is
            // exotic; it is the first thing every anti-bot check looks at, so the challenge was being failed
            // before its JavaScript even ran.
            foreach (var a in new[]
            {
                "--headless=new", "--disable-gpu", "--no-sandbox", "--disable-dev-shm-usage",
                "--remote-debugging-port=0",          // port 0 => the browser picks one and prints it
                $"--user-data-dir={profile}",
                // Stop advertising automation.
                "--disable-blink-features=AutomationControlled",
                "--disable-infobars",
                // A plausible client: real window, real language, no synthetic defaults.
                "--window-size=1920,1080",
                "--lang=en-US,en",
                $"--user-agent={DefaultUserAgent}",
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

            // Scrub the remaining automation tells before any page script runs.
            // addScriptToEvaluateOnNewDocument lands ahead of the challenge's own code, which is the only
            // moment these are still writable — patching after navigation is too late to matter.
            await SendAsync(ws, ++id, "Page.addScriptToEvaluateOnNewDocument", new { source = StealthScript },
                cts.Token, session);

            await SendAsync(ws, ++id, "Page.navigate", new { url }, cts.Token, session);

            // Let the interstitial run. It is a deliberate delay on Cloudflare's side, so polling faster
            // does not help; this simply has to outlast it.
            // Poll rather than sleeping once. A challenge that clears in 4 seconds shouldn't cost the full
            // window, and one that needs 25 shouldn't be abandoned at 15 — a single fixed pause is wrong in
            // both directions. The cookie read is a local DevTools call, so polling is nearly free.
            var settle = TimeSpan.FromSeconds(Math.Clamp(_opts.ChallengeSettleSeconds, 5, 60));
            var deadline = DateTime.UtcNow + settle;
            string? clearance = null;
            while (clearance is null && DateTime.UtcNow < deadline)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(2), cts.Token); }
                catch (OperationCanceledException) { return null; }
                clearance = FindClearance(await CallAsync(ws, ++id, "Network.getAllCookies", new { }, cts.Token, session));
            }
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

        // Ordered by likelihood of being genuine. chromium-browser is last because on Ubuntu it is
        // usually the snap stub, and preferring it would pick a binary that cannot run.
        foreach (var candidate in new[]
        {
            "/usr/bin/google-chrome-stable", "/usr/bin/google-chrome", "/usr/bin/chromium",
            "/usr/lib/chromium/chromium", "/usr/bin/chromium-browser"
        })
            if (File.Exists(candidate)) return candidate;

        return null;
    }

    /// <summary>
    /// Runs before any page script. Each line removes a specific, well-known automation tell: a headless
    /// browser reports navigator.webdriver = true, an empty plugin list, no window.chrome, and a permissions
    /// API that answers differently from a real one. Any single mismatch is enough to fail the challenge.
    /// </summary>
    private const string StealthScript = @"
        Object.defineProperty(navigator, 'webdriver', { get: () => undefined });
        Object.defineProperty(navigator, 'languages', { get: () => ['en-US', 'en'] });
        Object.defineProperty(navigator, 'plugins',   { get: () => [1, 2, 3, 4, 5] });
        window.chrome = window.chrome || { runtime: {} };
        const __q = navigator.permissions && navigator.permissions.query;
        if (__q) navigator.permissions.query = (p) =>
            p && p.name === 'notifications'
                ? Promise.resolve({ state: Notification.permission })
                : __q(p);
    ";

    private const string DefaultUserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
}
