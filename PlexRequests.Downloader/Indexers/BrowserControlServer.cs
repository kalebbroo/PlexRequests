using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Internal WebSocket endpoint for the web app's admin-only relay. It is intentionally not an ASP.NET
/// service and is never published to the host: one small listener avoids turning the worker into a second
/// web application while still working when it shares Gluetun's network namespace.
/// </summary>
public sealed class BrowserControlServer(
    IOptions<IndexerOptions> indexerOptions,
    IOptions<ApiOptions> apiOptions,
    IInteractiveBrowserTransport browser,
    ILogger<BrowserControlServer> logger) : BackgroundService
{
    private readonly string _prefix = NormalizePrefix(indexerOptions.Value.BrowserControlPrefix);
    private readonly TimeSpan _sessionTimeout = TimeSpan.FromMinutes(
        Math.Clamp(indexerOptions.Value.BrowserSessionTimeoutMinutes, 1, 15));
    private readonly string _apiKey = apiOptions.Value.Key;
    private HttpListener? _listener;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
        {
            logger.LogWarning("Interactive indexer browser disabled because Api:Key is empty");
            return;
        }

        _listener = new HttpListener();
        _listener.Prefixes.Add(_prefix);
        _listener.Start();
        logger.LogInformation("Interactive indexer browser listening internally at {Prefix}", _prefix);

        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                HttpListenerContext context;
                try { context = await _listener.GetContextAsync().WaitAsync(stoppingToken); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
                _ = HandleAsync(context, stoppingToken);
            }
        }
        finally
        {
            try { _listener.Stop(); } catch { }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken stoppingToken)
    {
        try
        {
            if (!Authorized(context.Request.Headers["X-Fulfillment-Key"]))
            {
                context.Response.StatusCode = 401;
                context.Response.Close();
                return;
            }
            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                return;
            }

            var upgraded = await context.AcceptWebSocketAsync(null);
            using var sessionLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            sessionLifetime.CancelAfter(_sessionTimeout);
            await browser.RunInteractiveSessionAsync(upgraded.WebSocket, sessionLifetime.Token);
        }
        catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
        {
            logger.LogInformation("Interactive indexer browser viewer reached its {Minutes}-minute limit",
                _sessionTimeout.TotalMinutes);
        }
        catch (WebSocketException ex)
        {
            // Closing a tab or losing mobile signal often ends without a WebSocket close frame. The
            // persistent browser is unaffected, so this is routine disconnect telemetry, not an outage.
            logger.LogDebug(ex, "Interactive indexer browser viewer disconnected");
        }
        catch (IOException ex)
        {
            logger.LogDebug(ex, "Interactive indexer browser viewer disconnected");
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Interactive indexer browser session failed");
            try { context.Response.Abort(); } catch { }
        }
    }

    private bool Authorized(string? provided)
    {
        if (string.IsNullOrEmpty(provided)) return false;
        var actual = Encoding.UTF8.GetBytes(provided);
        var expected = Encoding.UTF8.GetBytes(_apiKey);
        return actual.Length == expected.Length && CryptographicOperations.FixedTimeEquals(actual, expected);
    }

    private static string NormalizePrefix(string value)
    {
        var prefix = string.IsNullOrWhiteSpace(value) ? "http://*:9225/browser/" : value.Trim();
        return prefix.EndsWith('/') ? prefix : prefix + "/";
    }

    public override void Dispose()
    {
        try { _listener?.Close(); } catch { }
        base.Dispose();
    }
}
