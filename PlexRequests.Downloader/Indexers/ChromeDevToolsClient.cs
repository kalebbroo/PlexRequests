using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace PlexRequests.Downloader.Indexers;

/// <summary>
/// Small, dependency-free Chrome DevTools Protocol client. A single reader owns the socket and dispatches
/// replies by id, which is the important difference from the old challenge solver's send/read helper:
/// screencast events and input commands are concurrent, so multiple callers must never race ReceiveAsync.
/// </summary>
internal sealed class ChromeDevToolsClient : IAsyncDisposable
{
    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement?>> _pending = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private Task? _reader;
    private int _nextId;

    public event Func<JsonElement, Task>? EventReceived;

    public async Task ConnectAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        await _socket.ConnectAsync(endpoint, cancellationToken);
        _reader = ReadLoopAsync(_lifetime.Token);
    }

    public async Task<JsonElement?> CallAsync(string method, object parameters, CancellationToken cancellationToken,
        string? sessionId = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement?>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pending.TryAdd(id, completion)) throw new InvalidOperationException("Duplicate DevTools command id.");

        try
        {
            await SendCoreAsync(id, method, parameters, sessionId, cancellationToken);
            return await completion.Task.WaitAsync(cancellationToken);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Send a command whose acknowledgement is irrelevant (for example screencast-frame ACKs).</summary>
    public Task NotifyAsync(string method, object parameters, CancellationToken cancellationToken,
        string? sessionId = null) =>
        SendCoreAsync(Interlocked.Increment(ref _nextId), method, parameters, sessionId, cancellationToken);

    private async Task SendCoreAsync(int id, string method, object parameters, string? sessionId,
        CancellationToken cancellationToken)
    {
        var payload = sessionId is null
            ? JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters })
            : JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters, sessionId });

        await _sendGate.WaitAsync(cancellationToken);
        try
        {
            await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken);
        }
        finally
        {
            _sendGate.Release();
        }
    }

    private async Task ReadLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        var message = new MemoryStream();
        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                WebSocketReceiveResult result;
                do
                {
                    result = await _socket.ReceiveAsync(buffer, cancellationToken);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    await message.WriteAsync(buffer.AsMemory(0, result.Count), cancellationToken);
                } while (!result.EndOfMessage);

                using var document = JsonDocument.Parse(message.GetBuffer().AsMemory(0, checked((int)message.Length)));
                var root = document.RootElement;
                if (root.TryGetProperty("id", out var idElement))
                {
                    if (_pending.TryGetValue(idElement.GetInt32(), out var completion))
                    {
                        if (root.TryGetProperty("error", out var error))
                            completion.TrySetException(new InvalidOperationException(error.ToString()));
                        else
                            completion.TrySetResult(root.TryGetProperty("result", out var value) ? value.Clone() : null);
                    }
                    continue;
                }

                if (root.TryGetProperty("method", out _) && EventReceived is { } handler)
                {
                    var copy = root.Clone();
                    _ = DispatchEventAsync(handler, copy);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            foreach (var pending in _pending.Values) pending.TrySetException(ex);
        }
        finally
        {
            foreach (var pending in _pending.Values)
                pending.TrySetException(new WebSocketException("The Chrome DevTools connection closed."));
        }
    }

    private static async Task DispatchEventAsync(Func<JsonElement, Task> handler, JsonElement message)
    {
        try { await handler(message); }
        catch { /* an event consumer must never terminate the DevTools reader */ }
    }

    public async ValueTask DisposeAsync()
    {
        _lifetime.Cancel();
        if (_socket.State == WebSocketState.Open)
        {
            try { await _socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None); }
            catch { }
        }
        if (_reader is not null)
        {
            try { await _reader; } catch { }
        }
        _socket.Dispose();
        _sendGate.Dispose();
        _lifetime.Dispose();
    }
}
