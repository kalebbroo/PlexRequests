using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;

namespace PlexRequests.Downloader.Download;

/// <summary>
/// Deluge Web (deluge-web) JSON-RPC client. All calls POST to /json as
/// {"method","params","id"}; a session cookie is established by auth.login and kept in a shared
/// CookieContainer. Session expiry is handled transparently (re-login + retry once).
/// </summary>
public class DelugeDownloadClient(HttpClient http, IOptions<DelugeOptions> options, ILogger<DelugeDownloadClient> logger)
    : IDownloadClient
{
    private readonly HttpClient _http = http;
    private readonly DelugeOptions _opts = options.Value;
    private readonly ILogger<DelugeDownloadClient> _logger = logger;
    private int _id;

    public async Task<string?> AddMagnetAsync(string magnet, string? label, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        var result = await RpcAsync("core.add_torrent_magnet", new object[] { magnet, new Dictionary<string, object>() }, ct);
        var hash = result.ValueKind == JsonValueKind.String ? result.GetString() : null;
        if (string.IsNullOrWhiteSpace(hash))
        {
            _logger.LogWarning("Deluge did not return a torrent id when adding a magnet");
            return null;
        }

        if (!string.IsNullOrWhiteSpace(label))
        {
            // Label plugin is optional; create-then-assign, both best-effort.
            try { await RpcAsync("label.add", new object[] { label }, ct); } catch { /* may already exist / plugin off */ }
            try { await RpcAsync("label.set_torrent", new object[] { hash, label }, ct); }
            catch (Exception ex) { _logger.LogDebug(ex, "Deluge label assignment skipped"); }
        }
        return hash;
    }

    public async Task<DownloadStatus?> GetStatusAsync(string torrentId, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        var keys = new[] { "name", "state", "progress", "total_size", "is_finished", "download_location", "save_path", "files" };
        var result = await RpcAsync("core.get_torrent_status", new object[] { torrentId, keys }, ct);
        if (result.ValueKind != JsonValueKind.Object || !result.EnumerateObject().Any())
            return null;

        string name = Str(result, "name");
        string state = Str(result, "state");
        double progress = result.TryGetProperty("progress", out var pr) && pr.ValueKind == JsonValueKind.Number ? pr.GetDouble() : 0;
        long size = result.TryGetProperty("total_size", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0;
        bool finished = result.TryGetProperty("is_finished", out var f) && f.ValueKind == JsonValueKind.True;
        string? path = Str(result, "download_location");
        if (string.IsNullOrEmpty(path)) path = Str(result, "save_path");

        // The authoritative on-disk relative paths — the torrent's reported "name" is only a display
        // hint (from the magnet's dn= or resolved metadata) and can differ from what actually lands on
        // disk, so callers should prefer Files over Name when resolving the real import source path.
        var files = new List<string>();
        if (result.TryGetProperty("files", out var filesEl) && filesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var fileEl in filesEl.EnumerateArray())
            {
                var relPath = Str(fileEl, "path");
                if (!string.IsNullOrEmpty(relPath)) files.Add(relPath);
            }
        }

        return new DownloadStatus(state, progress, name, size, string.IsNullOrEmpty(path) ? null : path, finished, files);
    }

    public async Task<bool> RemoveAsync(string torrentId, bool removeData, CancellationToken ct)
    {
        await EnsureAuthAsync(ct);
        var result = await RpcAsync("core.remove_torrent", new object[] { torrentId, removeData }, ct);
        return result.ValueKind == JsonValueKind.True;
    }

    public async Task<bool> SetWantedFilesAsync(string torrentId, IReadOnlyList<bool> keep, CancellationToken ct)
    {
        if (keep.Count == 0) return false;
        await EnsureAuthAsync(ct);
        // Deluge file-priority scale: 0 = Skip (don't download), 4 = Normal. The array is positional,
        // indexed by the torrent's file index (same order as core.get_torrent_status "files").
        var priorities = keep.Select(k => (object)(k ? 4 : 0)).ToArray();
        var options = new Dictionary<string, object> { ["file_priorities"] = priorities };
        try
        {
            // set_torrent_options returns null on success (no "result"); an RPC error throws.
            await RpcAsync("core.set_torrent_options", new object[] { new[] { torrentId }, options }, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Deluge set_torrent_options(file_priorities) failed for {TorrentId}", torrentId);
            return false;
        }
    }

    // ----- JSON-RPC plumbing -----

    private async Task EnsureAuthAsync(CancellationToken ct)
    {
        try
        {
            var r = await RpcAsync("auth.check_session", Array.Empty<object>(), ct, retryAuth: false);
            if (r.ValueKind == JsonValueKind.True) return;
        }
        catch { /* fall through to login */ }
        await LoginAsync(ct);
    }

    private async Task LoginAsync(CancellationToken ct)
    {
        var result = await PostAsync("auth.login", new object[] { _opts.Password }, ct);
        if (result.ValueKind != JsonValueKind.True)
            throw new InvalidOperationException("Deluge auth.login failed (check password/URL)");
    }

    private async Task<JsonElement> RpcAsync(string method, object[] @params, CancellationToken ct, bool retryAuth = true)
    {
        try
        {
            return await PostAsync(method, @params, ct);
        }
        catch (DelugeAuthException) when (retryAuth)
        {
            _logger.LogInformation("Deluge session expired; re-authenticating");
            await LoginAsync(ct);
            return await PostAsync(method, @params, ct);
        }
    }

    private async Task<JsonElement> PostAsync(string method, object[] @params, CancellationToken ct)
    {
        var payload = new { method, @params, id = Interlocked.Increment(ref _id) };
        using var resp = await _http.PostAsJsonAsync("/json", payload, ct);
        resp.EnsureSuccessStatusCode();
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
        var root = doc.RootElement;

        if (root.TryGetProperty("error", out var err) && err.ValueKind == JsonValueKind.Object)
        {
            var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown error";
            if ((msg ?? "").Contains("auth", StringComparison.OrdinalIgnoreCase) || (msg ?? "").Contains("session", StringComparison.OrdinalIgnoreCase))
                throw new DelugeAuthException(msg ?? "not authenticated");
            throw new InvalidOperationException($"Deluge RPC '{method}' failed: {msg}");
        }
        return root.TryGetProperty("result", out var result) ? result.Clone() : default;
    }

    private static string Str(JsonElement obj, string prop) =>
        obj.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? string.Empty : string.Empty;

    private sealed class DelugeAuthException(string message) : Exception(message);
}
