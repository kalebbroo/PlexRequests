using System.Text.Json;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Worker;

/// <summary>One torrent backing a job: its Deluge id, the season/episode it covers, and whether it imported.</summary>
public record TorrentItem(string TorrentId, int? Season, int? Episode, bool IsPack, bool Imported = false);

/// <summary>An in-flight download: the claimed job plus the one-or-more torrents fulfilling it.</summary>
public record ActiveJobRecord(FulfillmentJobDto Job, List<TorrentItem> Torrents);

public interface IJobStateStore
{
    Task SaveAsync(ActiveJobRecord record, CancellationToken ct);
    Task RemoveAsync(int jobId, CancellationToken ct);
    Task<IReadOnlyList<ActiveJobRecord>> GetAllAsync(CancellationToken ct);
}

/// <summary>
/// Persists active jobs to a JSON file so a worker restart resumes monitoring in-flight downloads
/// (the web-side stale-claim reaper is the backstop if the worker dies for good).
/// </summary>
public class JsonJobStateStore : IJobStateStore
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private readonly ILogger<JsonJobStateStore> _logger;

    public JsonJobStateStore(IOptions<WorkerOptions> options, ILogger<JsonJobStateStore> logger)
    {
        _path = Path.GetFullPath(options.Value.StatePath);
        _logger = logger;
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
    }

    public async Task SaveAsync(ActiveJobRecord record, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            all.RemoveAll(r => r.Job.Id == record.Job.Id);
            all.Add(record);
            await WriteAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task RemoveAsync(int jobId, CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try
        {
            var all = await LoadAsync(ct);
            if (all.RemoveAll(r => r.Job.Id == jobId) > 0) await WriteAsync(all, ct);
        }
        finally { _lock.Release(); }
    }

    public async Task<IReadOnlyList<ActiveJobRecord>> GetAllAsync(CancellationToken ct)
    {
        await _lock.WaitAsync(ct);
        try { return await LoadAsync(ct); }
        finally { _lock.Release(); }
    }

    private async Task<List<ActiveJobRecord>> LoadAsync(CancellationToken ct)
    {
        if (!File.Exists(_path)) return new();
        try
        {
            await using var fs = File.OpenRead(_path);
            return await JsonSerializer.DeserializeAsync<List<ActiveJobRecord>>(fs, cancellationToken: ct) ?? new();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not read state file {Path}; starting empty", _path);
            return new();
        }
    }

    private async Task WriteAsync(List<ActiveJobRecord> all, CancellationToken ct)
    {
        var tmp = _path + ".tmp";
        await using (var fs = File.Create(tmp))
            await JsonSerializer.SerializeAsync(fs, all, cancellationToken: ct);
        File.Move(tmp, _path, overwrite: true); // atomic replace
    }
}
