using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Persistent, manually verifiable Chrome session for 1337x. Search and interactive viewing deliberately
/// share one page, profile and network namespace: copying a cookie into HttpClient loses browser/TLS state
/// and is exactly what failed in production. Only this provider pays the browser cost.
/// </summary>
public sealed class PersistentBrowserTransport(
    IOptions<IndexerOptions> options,
    ILogger<PersistentBrowserTransport> logger) : IInteractiveBrowserTransport, IAsyncDisposable
{
    private readonly IndexerOptions _options = options.Value;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private readonly SemaphoreSlim _pageGate = new(1, 1);
    private readonly SemaphoreSlim _viewerGate = new(1, 1);
    private Process? _display;
    private Process? _browser;
    private ChromeDevToolsClient? _devTools;
    private string? _sessionId;
    private Task? _browserLogDrain;

    private int Width => Math.Clamp(_options.BrowserViewportWidth, 800, 1920);
    private int Height => Math.Clamp(_options.BrowserViewportHeight, 600, 1080);
    private Uri BaseUri => new(_options.X1337xBaseUrl.TrimEnd('/') + "/");

    public async Task<string> GetStringAsync(string url, CancellationToken cancellationToken)
    {
        await _pageGate.WaitAsync(cancellationToken);
        try
        {
            await EnsureStartedAsync(cancellationToken);
            var target = ResolveTarget(url);
            await NavigateAsync(target, cancellationToken);
            var html = await EvaluateStringAsync("document.documentElement.outerHTML", cancellationToken);
            if (LooksLikeChallenge(html))
                throw new IndexerBlockedException(IndexerBlockReason.CloudflareChallenge,
                    "1337x needs an administrator to verify its persistent browser session");
            return html;
        }
        finally
        {
            _pageGate.Release();
        }
    }

    public async Task RunInteractiveSessionAsync(WebSocket adminSocket, CancellationToken cancellationToken)
    {
        if (!await _viewerGate.WaitAsync(0, cancellationToken))
        {
            await SendAsync(adminSocket, new { type = "status", state = "busy", message = "Another administrator is already using the browser." }, cancellationToken);
            await adminSocket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "browser busy", cancellationToken);
            return;
        }

        await _pageGate.WaitAsync(cancellationToken);
        try
        {
            using var sendGate = new SemaphoreSlim(1, 1);
            await EnsureStartedAsync(cancellationToken);
            var current = await EvaluateStringAsync("location.href", cancellationToken);
            if (!IsAllowedLocation(current, BaseUri)) await NavigateAsync(BaseUri, cancellationToken);

            var frames = Channel.CreateBounded<string>(new BoundedChannelOptions(1)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false
            });

            async Task OnEvent(JsonElement message)
            {
                if (!message.TryGetProperty("method", out var method)
                    || !message.TryGetProperty("params", out var parameters)) return;

                if (method.GetString() == "Page.frameNavigated"
                    && parameters.TryGetProperty("frame", out var frame)
                    && !frame.TryGetProperty("parentId", out _)
                    && frame.TryGetProperty("url", out var navigated)
                    && !IsAllowedLocation(navigated.GetString(), BaseUri))
                {
                    // The viewer has no address bar, but a page link could still point elsewhere. Bounce
                    // main-frame navigation back to 1337x; subframes may load Cloudflare's challenge host.
                    _ = ReturnToOriginAsync();
                    return;
                }

                if (method.GetString() != "Page.screencastFrame") return;

                if (parameters.TryGetProperty("sessionId", out var frameId))
                    _ = AcknowledgeFrameAsync(frameId.GetInt32());

                if (parameters.TryGetProperty("data", out var data) && data.GetString() is { Length: > 0 } jpeg)
                    frames.Writer.TryWrite(jpeg);
                await Task.CompletedTask;
            }

            _devTools!.EventReceived += OnEvent;
            try
            {
                await _devTools.CallAsync("Page.startScreencast",
                    new { format = "jpeg", quality = 72, maxWidth = Width, maxHeight = Height, everyNthFrame = 1 },
                    cancellationToken, _sessionId);

                await SendAsync(adminSocket, new
                {
                    type = "status", state = "connected",
                    message = "This is Chrome inside the downloader VPN. Complete the check, then verify the session."
                }, cancellationToken, sendGate);

                using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                var sendFrames = SendFramesAsync(adminSocket, frames.Reader, sendGate, sessionLifetime.Token);
                var receiveInput = ReceiveInputAsync(adminSocket, sendGate, sessionLifetime.Token);
                await Task.WhenAny(sendFrames, receiveInput);
                sessionLifetime.Cancel();
                frames.Writer.TryComplete();
                try { await Task.WhenAll(sendFrames, receiveInput); }
                catch (OperationCanceledException) when (sessionLifetime.IsCancellationRequested) { }
            }
            finally
            {
                _devTools.EventReceived -= OnEvent;
                try { await _devTools.CallAsync("Page.stopScreencast", new { }, CancellationToken.None, _sessionId); }
                catch { }
                if (adminSocket.State == WebSocketState.Open)
                {
                    try { await adminSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "viewer closed", CancellationToken.None); }
                    catch { }
                }
            }
        }
        finally
        {
            _pageGate.Release();
            _viewerGate.Release();
        }
    }

    private async Task ReturnToOriginAsync()
    {
        try { await NavigateAsync(BaseUri, CancellationToken.None); }
        catch { }
    }

    private async Task AcknowledgeFrameAsync(int frameId)
    {
        try
        {
            if (_devTools is not null)
                await _devTools.NotifyAsync("Page.screencastFrameAck", new { sessionId = frameId },
                    CancellationToken.None, _sessionId);
        }
        catch { }
    }

    private async Task SendFramesAsync(WebSocket socket, ChannelReader<string> frames, SemaphoreSlim sendGate,
        CancellationToken cancellationToken)
    {
        await foreach (var frame in frames.ReadAllAsync(cancellationToken))
        {
            if (socket.State != WebSocketState.Open) return;
            await SendAsync(socket, new { type = "frame", data = frame, width = Width, height = Height }, cancellationToken, sendGate);
        }
    }

    private async Task ReceiveInputAsync(WebSocket socket, SemaphoreSlim sendGate, CancellationToken cancellationToken)
    {
        while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
        {
            var text = await ReceiveTextAsync(socket, cancellationToken);
            if (text is null) return;

            using var document = JsonDocument.Parse(text);
            var root = document.RootElement;
            var type = root.TryGetProperty("type", out var typeElement) ? typeElement.GetString() : null;
            switch (type)
            {
                case "pointer":
                    var x = Math.Clamp(root.GetProperty("x").GetDouble(), 0, 1) * Width;
                    var y = Math.Clamp(root.GetProperty("y").GetDouble(), 0, 1) * Height;
                    await _devTools!.CallAsync("Input.dispatchMouseEvent",
                        new { type = "mouseMoved", x, y, button = "none" }, cancellationToken, _sessionId);
                    await _devTools.CallAsync("Input.dispatchMouseEvent",
                        new { type = "mousePressed", x, y, button = "left", clickCount = 1 }, cancellationToken, _sessionId);
                    await _devTools.CallAsync("Input.dispatchMouseEvent",
                        new { type = "mouseReleased", x, y, button = "left", clickCount = 1 }, cancellationToken, _sessionId);
                    break;

                case "key":
                    var key = root.TryGetProperty("key", out var keyElement) ? keyElement.GetString() ?? "" : "";
                    var code = root.TryGetProperty("code", out var codeElement) ? codeElement.GetString() ?? "" : "";
                    if (key.Length <= 16 && code.Length <= 32)
                    {
                        await _devTools!.CallAsync("Input.dispatchKeyEvent",
                            new { type = "keyDown", key, code, text = key.Length == 1 ? key : "" }, cancellationToken, _sessionId);
                        await _devTools.CallAsync("Input.dispatchKeyEvent",
                            new { type = "keyUp", key, code }, cancellationToken, _sessionId);
                    }
                    break;

                case "reload":
                    await NavigateAsync(BaseUri, cancellationToken);
                    break;

                case "verify":
                    var location = await EvaluateStringAsync("location.href", cancellationToken);
                    var html = await EvaluateStringAsync("document.documentElement.outerHTML", cancellationToken);
                    var verified = IsAllowedLocation(location, BaseUri) && !LooksLikeChallenge(html)
                                   && html.Contains("torrent", StringComparison.OrdinalIgnoreCase);
                    await SendAsync(socket, new
                    {
                        type = "status",
                        state = verified ? "verified" : "challenge",
                        message = verified
                            ? "1337x is available in the persistent downloader browser."
                            : "The challenge is still present. Complete it and try Verify again."
                    }, cancellationToken, sendGate);
                    break;
            }
        }
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_browser is { HasExited: false } && _devTools is not null && _sessionId is not null) return;

        await _startGate.WaitAsync(cancellationToken);
        try
        {
            if (_browser is { HasExited: false } && _devTools is not null && _sessionId is not null) return;
            await StopBrowserAsync();

            var browserPath = ResolveBrowserPath()
                ?? throw new InvalidOperationException("Google Chrome is not installed in the downloader container.");
            var displayPath = File.Exists("/usr/bin/Xvfb") ? "/usr/bin/Xvfb" : null;
            if (displayPath is null) throw new InvalidOperationException("Xvfb is not installed in the downloader container.");

            var display = ":99";
            _display = Process.Start(new ProcessStartInfo
            {
                FileName = displayPath,
                UseShellExecute = false,
                ArgumentList = { display, "-screen", "0", $"{Width}x{Height}x24", "-nolisten", "tcp" }
            }) ?? throw new InvalidOperationException("Could not start the browser display.");
            await Task.Delay(300, cancellationToken);
            if (_display.HasExited) throw new InvalidOperationException("The browser display exited during startup.");

            var profile = Path.GetFullPath(_options.BrowserProfilePath);
            Directory.CreateDirectory(profile);
            var start = new ProcessStartInfo
            {
                FileName = browserPath,
                RedirectStandardError = true,
                UseShellExecute = false
            };
            start.Environment.Clear();
            start.Environment["DISPLAY"] = display;
            start.Environment["HOME"] = profile;
            start.Environment["LANG"] = "en_US.UTF-8";
            start.Environment["PATH"] = "/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin";
            foreach (var argument in new[]
            {
                "--no-sandbox", "--disable-dev-shm-usage",
                "--no-first-run", "--no-default-browser-check", "--restore-last-session",
                // A non-zero port matters: Chrome advertises navigator.webdriver when remote debugging
                // uses port 0. The listener remains loopback-only inside this container.
                "--remote-debugging-address=127.0.0.1", "--remote-debugging-port=9223",
                $"--user-data-dir={profile}",
                $"--window-size={Width},{Height}", "--lang=en-US,en", "about:blank"
            }) start.ArgumentList.Add(argument);

            _browser = Process.Start(start) ?? throw new InvalidOperationException("Could not start Google Chrome.");
            var endpoint = await ReadDevToolsEndpointAsync(_browser, cancellationToken)
                ?? throw new InvalidOperationException("Chrome did not expose its DevTools endpoint.");
            _browserLogDrain = DrainBrowserLogAsync(_browser, CancellationToken.None);

            _devTools = new ChromeDevToolsClient();
            await _devTools.ConnectAsync(new Uri(endpoint), cancellationToken);
            var target = await _devTools.CallAsync("Target.createTarget", new { url = "about:blank" }, cancellationToken);
            var targetId = target?.GetProperty("targetId").GetString()
                ?? throw new InvalidOperationException("Chrome did not create a page target.");
            var attached = await _devTools.CallAsync("Target.attachToTarget",
                new { targetId, flatten = true }, cancellationToken);
            _sessionId = attached?.GetProperty("sessionId").GetString()
                ?? throw new InvalidOperationException("Chrome did not attach to its page target.");
            await _devTools.CallAsync("Page.enable", new { }, cancellationToken, _sessionId);
            await _devTools.CallAsync("Network.enable", new { }, cancellationToken, _sessionId);
            await _devTools.CallAsync("Browser.setDownloadBehavior", new { behavior = "deny" }, cancellationToken);
            await _devTools.CallAsync("Emulation.setDeviceMetricsOverride",
                new { width = Width, height = Height, deviceScaleFactor = 1, mobile = false }, cancellationToken, _sessionId);

            logger.LogInformation("Persistent 1337x browser started with profile {Profile}", profile);
        }
        catch
        {
            await StopBrowserAsync();
            throw;
        }
        finally
        {
            _startGate.Release();
        }
    }

    private async Task NavigateAsync(Uri target, CancellationToken cancellationToken)
    {
        var marker = Guid.NewGuid().ToString("N");
        await _devTools!.CallAsync("Runtime.evaluate",
            new { expression = $"window.__plexNavigationMarker='{marker}'" }, cancellationToken, _sessionId);
        var result = await _devTools!.CallAsync("Page.navigate", new { url = target.ToString() }, cancellationToken, _sessionId);
        if (result is { } value && value.TryGetProperty("errorText", out var error))
            throw new HttpRequestException($"Chrome could not navigate to 1337x: {error.GetString()}");

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 60));
        var loaded = false;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                // Page.navigate replies before the new document is necessarily installed. Waiting for a
                // marker from the old JS context to disappear prevents us from parsing the previous page.
                var oldMarker = await EvaluateStringAsync("window.__plexNavigationMarker || ''", cancellationToken);
                var ready = await EvaluateStringAsync("document.readyState", cancellationToken);
                if (oldMarker != marker && ready is ("complete" or "interactive")) { loaded = true; break; }
            }
            catch (InvalidOperationException)
            {
                // Expected for the brief moment Chrome destroys the old execution context.
            }
            await Task.Delay(150, cancellationToken);
        }
        if (!loaded) throw new TimeoutException($"Chrome did not finish loading 1337x within {_options.TimeoutSeconds} seconds.");
        // Let client-rendered table rows and Cloudflare redirects settle after DOM readiness.
        await Task.Delay(500, cancellationToken);
        var current = await EvaluateStringAsync("location.href", cancellationToken);
        if (!IsAllowedLocation(current, BaseUri))
            throw new InvalidOperationException("The 1337x browser was redirected outside its configured origin.");
    }

    private async Task<string> EvaluateStringAsync(string expression, CancellationToken cancellationToken)
    {
        var response = await _devTools!.CallAsync("Runtime.evaluate",
            new { expression, returnByValue = true }, cancellationToken, _sessionId);
        if (response is not { } root || !root.TryGetProperty("result", out var result)
            || !result.TryGetProperty("value", out var value)) return string.Empty;
        return value.GetString() ?? value.ToString();
    }

    private Uri ResolveTarget(string value)
    {
        var target = Uri.TryCreate(value, UriKind.Absolute, out var absolute) ? absolute : new Uri(BaseUri, value);
        if (!IsAllowedLocation(target.ToString(), BaseUri))
            throw new InvalidOperationException("The 1337x browser transport refused navigation outside its configured origin.");
        return target;
    }

    public static bool IsAllowedLocation(string? value, Uri configuredOrigin)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        return uri.Scheme == configuredOrigin.Scheme
               && uri.Host.Equals(configuredOrigin.Host, StringComparison.OrdinalIgnoreCase)
               && uri.Port == configuredOrigin.Port;
    }

    public static bool LooksLikeChallenge(string html)
    {
        // A healthy result/detail page wins even if the site's shared layout happens to load a Turnstile
        // script. Structural content is stronger evidence than a generic script name.
        if (html.Contains("table-list", StringComparison.OrdinalIgnoreCase)
            || html.Contains("magnet:?xt=urn:btih:", StringComparison.OrdinalIgnoreCase)) return false;
        return html.Contains("cdn-cgi/challenge", StringComparison.OrdinalIgnoreCase)
               || html.Contains("__cf_chl", StringComparison.OrdinalIgnoreCase)
               || html.Contains("cf-turnstile", StringComparison.OrdinalIgnoreCase)
               || (html.Contains("<title", StringComparison.OrdinalIgnoreCase)
                   && (html.Contains("Just a moment", StringComparison.OrdinalIgnoreCase)
                       || html.Contains("Attention Required", StringComparison.OrdinalIgnoreCase)));
    }

    private string? ResolveBrowserPath()
    {
        if (!string.IsNullOrWhiteSpace(_options.BrowserPath))
            return File.Exists(_options.BrowserPath) ? _options.BrowserPath : null;
        return new[] { "/usr/bin/google-chrome-stable", "/usr/bin/google-chrome", "/usr/bin/chromium" }
            .FirstOrDefault(File.Exists);
    }

    private static async Task<string?> ReadDevToolsEndpointAsync(Process process, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await process.StandardError.ReadLineAsync(cancellationToken);
            if (line is null) return null;
            var marker = line.IndexOf("ws://", StringComparison.Ordinal);
            if (marker >= 0) return line[marker..].Trim();
        }
        return null;
    }

    private async Task DrainBrowserLogAsync(Process process, CancellationToken cancellationToken)
    {
        try
        {
            while (!process.HasExited && !cancellationToken.IsCancellationRequested)
            {
                var line = await process.StandardError.ReadLineAsync(cancellationToken);
                if (line is null) break;
                logger.LogTrace("Chrome: {Line}", line);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, cancellationToken);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text) continue;
            await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
            if (message.Length > 64 * 1024) throw new WebSocketException("Browser control message was too large.");
        } while (!result.EndOfMessage);
        return Encoding.UTF8.GetString(message.GetBuffer(), 0, checked((int)message.Length));
    }

    private static async Task SendAsync(WebSocket socket, object value, CancellationToken cancellationToken,
        SemaphoreSlim? sendGate = null)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value);
        if (sendGate is not null) await sendGate.WaitAsync(cancellationToken);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken); }
        finally { sendGate?.Release(); }
    }

    private async Task StopBrowserAsync()
    {
        _sessionId = null;
        if (_devTools is not null) { await _devTools.DisposeAsync(); _devTools = null; }
        if (_browser is not null)
        {
            try { if (!_browser.HasExited) _browser.Kill(true); } catch { }
            _browser.Dispose();
            _browser = null;
        }
        if (_browserLogDrain is not null) { try { await _browserLogDrain.WaitAsync(TimeSpan.FromSeconds(2)); } catch { } _browserLogDrain = null; }
        if (_display is not null)
        {
            try { if (!_display.HasExited) _display.Kill(true); } catch { }
            _display.Dispose();
            _display = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopBrowserAsync();
        _startGate.Dispose();
        _pageGate.Dispose();
        _viewerGate.Dispose();
    }
}
