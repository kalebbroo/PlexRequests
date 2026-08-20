using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Download;

/// <summary>Restart-safe direct-audio backend around yt-dlp. Each transfer owns a small atomic manifest and
/// deterministic staging directory on the downloader-state volume. A process restart therefore resumes
/// missing tracks instead of losing the job or redownloading a completed album.</summary>
public sealed partial class YouTubeMusicAcquisitionBackend : IAcquisitionBackend
{
    // yt-dlp only reports success after the media stream has been finalized. Keep a small floor to reject
    // empty/corrupt artifacts without dropping legitimate short intros, transitions, or spoken tracks.
    private const long MinimumCompletedFileBytes = 16 * 1024;
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".m4a", ".aac", ".opus", ".ogg", ".mp3", ".webm" };
    private readonly DirectAudioOptions _options;
    private readonly ILogger<YouTubeMusicAcquisitionBackend> _logger;
    private readonly CancellationToken _stoppingToken;
    private readonly string _root;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stateLocks = new(StringComparer.Ordinal);

    public YouTubeMusicAcquisitionBackend(IOptions<DirectAudioOptions> options,
        IHostApplicationLifetime lifetime, ILogger<YouTubeMusicAcquisitionBackend> logger)
    {
        _options = options.Value;
        _logger = logger;
        _stoppingToken = lifetime.ApplicationStopping;
        _root = Path.GetFullPath(_options.StatePath);
        _concurrency = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrent, 1, 8));
        Directory.CreateDirectory(_root);
    }

    public AcquisitionProtocol Protocol => AcquisitionProtocol.DirectAudio;
    public AcquisitionBackendCapabilities Capabilities { get; } = new(false, false, false);

    public async Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct)
    {
        if (!_options.Enabled || request.Resource.Protocol != Protocol
            || !YouTubeMusicLocator.TryDecode(request.Resource.Locator, out var locator)
            || string.IsNullOrWhiteSpace(request.Resource.SourceId)) return null;

        var identity = $"{request.CorrelationId ?? Guid.NewGuid().ToString("N")}|{request.Resource.SourceId}";
        var transferId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
        var gate = StateLock(transferId);
        await gate.WaitAsync(ct);
        try
        {
            var existing = await ReadStateAsync(transferId, ct);
            if (existing is null)
            {
                Directory.CreateDirectory(PayloadPath(transferId));
                existing = new DirectAudioState
                {
                    TransferId = transferId,
                    DisplayName = request.DisplayName,
                    SourceId = request.Resource.SourceId,
                    Tracks = locator.Tracks.ToList(),
                    Phase = DirectAudioPhase.Queued,
                    UpdatedAt = DateTime.UtcNow
                };
                await WriteStateAsync(existing, ct);
            }
        }
        finally { gate.Release(); }

        StartWorker(transferId);
        return transferId;
    }

    public async Task<TransferStatus?> GetStatusAsync(string transferId, CancellationToken ct)
    {
        var state = await ReadStateAsync(transferId, ct);
        if (state is null) return null;
        if (state.Phase is DirectAudioPhase.Queued or DirectAudioPhase.Downloading) StartWorker(transferId);

        var files = CompletedFiles(transferId);
        var total = files.Sum(SafeLength);
        return new TransferStatus(
            state.Phase == DirectAudioPhase.Failed ? "Error" : state.Phase.ToString(),
            state.Phase == DirectAudioPhase.Completed ? 100 : Math.Clamp(state.Progress, 0, 99.9),
            state.DisplayName,
            total,
            PayloadPath(transferId),
            state.Phase == DirectAudioPhase.Completed,
            files.Select(x => Path.GetFileName(x)!).ToList(),
            ProviderStatus: state.Error);
    }

    public TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt,
        DateTime? progressChangedAt, DateTime now)
    {
        if (status is null) return new(TransferVerdict.Missing, "Direct-audio transfer state is missing");
        if (status.IsFinished) return new(TransferVerdict.Import);
        if (status.State.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return new(TransferVerdict.Fail, status.ProviderStatus ?? "Direct-audio extraction failed", Blocklist: true);
        return new(TransferVerdict.Wait);
    }

    public async Task<bool> RemoveAsync(string transferId, bool removeData, CancellationToken ct)
    {
        if (_cancellations.TryGetValue(transferId, out var cancellation)) cancellation.Cancel();
        if (_workers.TryGetValue(transferId, out var worker))
        {
            try { await worker.WaitAsync(TimeSpan.FromSeconds(10), ct); }
            catch (TimeoutException) { return false; }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested) { }
        }
        if (!removeData) return true;
        var path = TransferPath(transferId);
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        _stateLocks.TryRemove(transferId, out var stateLock);
        stateLock?.Dispose();
        return true;
    }

    public Task<bool> SetWantedFilesAsync(string transferId, IReadOnlyList<bool> keep, CancellationToken ct) =>
        Task.FromResult(false);

    private void StartWorker(string transferId)
    {
        if (_workers.ContainsKey(transferId)) return;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_stoppingToken);
        if (!_cancellations.TryAdd(transferId, cancellation))
        {
            cancellation.Dispose();
            return;
        }
        var worker = Task.Run(() => RunAndReleaseAsync(transferId, cancellation), CancellationToken.None);
        if (!_workers.TryAdd(transferId, worker))
        {
            cancellation.Cancel();
            _cancellations.TryRemove(transferId, out _);
            cancellation.Dispose();
        }
    }

    private async Task RunAndReleaseAsync(string transferId, CancellationTokenSource cancellation)
    {
        try { await RunAsync(transferId, cancellation.Token); }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Direct-audio transfer {TransferId} crashed", transferId);
            await MarkFailedAsync(transferId, ex.Message, CancellationToken.None);
        }
        finally
        {
            _workers.TryRemove(transferId, out _);
            _cancellations.TryRemove(transferId, out _);
            cancellation.Dispose();
        }
    }

    private async Task RunAsync(string transferId, CancellationToken ct)
    {
        await _concurrency.WaitAsync(ct);
        try
        {
            var maxAttempts = Math.Clamp(_options.MaxAttempts, 1, 10);
            for (var attempt = 1; attempt <= maxAttempts; attempt++)
            {
                var state = await ReadStateAsync(transferId, ct);
                if (state is null || state.Phase is DirectAudioPhase.Completed or DirectAudioPhase.Failed) return;
                state.Attempts = attempt;
                state.Phase = DirectAudioPhase.Downloading;
                state.Error = null;
                await WriteStateLockedAsync(state, ct);

                try
                {
                    for (var index = 0; index < state.Tracks.Count; index++)
                    {
                        var track = state.Tracks[index];
                        if (FindTrackFile(transferId, track.VideoId) is not null)
                        {
                            state.Progress = (index + 1d) / state.Tracks.Count * 100;
                            await WriteStateLockedAsync(state, ct);
                            continue;
                        }
                        state.CurrentTrack = track.Title;
                        state.Progress = index * 100d / state.Tracks.Count;
                        await WriteStateLockedAsync(state, ct);
                        await DownloadTrackAsync(transferId, track, index, state.Tracks.Count,
                            progress => UpdateProgressAsync(state, index, progress, ct), ct);
                        if (FindTrackFile(transferId, track.VideoId) is null)
                            throw new InvalidOperationException($"yt-dlp exited without producing audio for {track.VideoId}");
                        state.Progress = (index + 1d) / state.Tracks.Count * 100;
                        await WriteStateLockedAsync(state, ct);
                    }

                    if (CompletedFiles(transferId).Count < state.Tracks.Count)
                        throw new InvalidOperationException($"Expected {state.Tracks.Count} audio files but found {CompletedFiles(transferId).Count}");
                    state.Phase = DirectAudioPhase.Completed;
                    state.Progress = 100;
                    state.CurrentTrack = null;
                    state.Error = null;
                    await WriteStateLockedAsync(state, ct);
                    _logger.LogInformation("Direct-audio transfer {TransferId} completed with {Count} track(s)",
                        transferId, state.Tracks.Count);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
                {
                    state.Error = ex.Message;
                    await WriteStateLockedAsync(state, ct);
                    _logger.LogWarning(ex, "Direct-audio transfer {TransferId} attempt {Attempt}/{Max} failed; retrying",
                        transferId, attempt, maxAttempts);
                    await Task.Delay(TimeSpan.FromSeconds(Math.Pow(2, attempt) * 5), ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    await MarkFailedAsync(transferId, ex.Message, ct);
                    return;
                }
            }
        }
        finally { _concurrency.Release(); }
    }

    private async Task DownloadTrackAsync(string transferId, YouTubeMusicTrack track, int index, int total,
        Func<double, Task> progress, CancellationToken ct)
    {
        var safeTitle = Sanitize(track.Title);
        var output = Path.Combine(PayloadPath(transferId),
            $"{Math.Max(1, track.DiscNumber):00}-{Math.Max(1, track.TrackNumber):00} - {safeTitle} [{track.VideoId}].%(ext)s");
        var start = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var arg in new[]
        {
            "--ignore-config", "--no-playlist", "--newline", "--continue", "--no-overwrites",
            "--js-runtimes", "deno",
            "--socket-timeout", "20", "--retries", "3", "--fragment-retries", "3",
            "--progress-template", "download:%(progress._percent_str)s",
            // YouTube publishes a native AAC/M4A audio stream for music. Selecting it directly avoids a
            // lossy transcode and keeps the container free of a 500 MB ffmpeg dependency tree.
            "--format", "bestaudio[ext=m4a]", "--output", output,
            $"https://music.youtube.com/watch?v={track.VideoId}"
        }) start.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("yt-dlp did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"Could not start {_options.ExecutablePath}: {ex.Message}", ex);
        }

        string? lastError = null;
        var stdout = ReadLinesAsync(process.StandardOutput, async line =>
        {
            var match = ProgressRegex().Match(line);
            if (match.Success && double.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var percent))
                await progress(percent);
        }, ct);
        var stderr = ReadLinesAsync(process.StandardError, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) lastError = line.Trim();
            return Task.CompletedTask;
        }, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.TrackTimeoutMinutes, 2, 120)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!ct.IsCancellationRequested)
                throw new TimeoutException($"Track {index + 1}/{total} exceeded the configured timeout");
            throw;
        }
        await Task.WhenAll(stdout, stderr);
        if (process.ExitCode != 0)
            throw new InvalidOperationException(lastError ?? $"yt-dlp exited with code {process.ExitCode}");
    }

    private static async Task ReadLinesAsync(StreamReader reader, Func<string, Task> receive, CancellationToken ct)
    {
        while (await reader.ReadLineAsync(ct) is { } line) await receive(line);
    }

    private async Task UpdateProgressAsync(DirectAudioState state, int completed, double trackProgress,
        CancellationToken ct)
    {
        var overall = (completed + Math.Clamp(trackProgress, 0, 100) / 100d) / state.Tracks.Count * 100;
        if (overall < state.Progress + 0.5) return;
        state.Progress = overall;
        await WriteStateLockedAsync(state, ct);
    }

    private async Task MarkFailedAsync(string transferId, string error, CancellationToken ct)
    {
        var state = await ReadStateAsync(transferId, ct);
        if (state is null) return;
        state.Phase = DirectAudioPhase.Failed;
        state.Error = error.Length > 2000 ? error[..2000] : error;
        state.CurrentTrack = null;
        await WriteStateLockedAsync(state, ct);
    }

    private List<string> CompletedFiles(string transferId) => Directory.Exists(PayloadPath(transferId))
        ? Directory.EnumerateFiles(PayloadPath(transferId), "*", SearchOption.TopDirectoryOnly)
            .Where(x => AudioExtensions.Contains(Path.GetExtension(x)) && SafeLength(x) >= MinimumCompletedFileBytes)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        : new();

    private string? FindTrackFile(string transferId, string videoId) => CompletedFiles(transferId)
        .FirstOrDefault(x => Path.GetFileName(x).Contains($"[{videoId}]", StringComparison.Ordinal));

    private async Task<DirectAudioState?> ReadStateAsync(string transferId, CancellationToken ct)
    {
        var path = StatePath(transferId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DirectAudioState>(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Direct-audio state {TransferId} is corrupt", transferId);
            return null;
        }
    }

    private async Task WriteStateLockedAsync(DirectAudioState state, CancellationToken ct)
    {
        var gate = StateLock(state.TransferId);
        await gate.WaitAsync(ct);
        try { await WriteStateAsync(state, ct); }
        finally { gate.Release(); }
    }

    private async Task WriteStateAsync(DirectAudioState state, CancellationToken ct)
    {
        state.UpdatedAt = DateTime.UtcNow;
        Directory.CreateDirectory(TransferPath(state.TransferId));
        var path = StatePath(state.TransferId);
        var temporary = path + ".tmp";
        await using (var stream = File.Create(temporary))
            await JsonSerializer.SerializeAsync(stream, state, cancellationToken: ct);
        File.Move(temporary, path, overwrite: true);
    }

    private SemaphoreSlim StateLock(string transferId) => _stateLocks.GetOrAdd(transferId, _ => new SemaphoreSlim(1, 1));
    private string TransferPath(string transferId) => Path.Combine(_root, transferId);
    private string PayloadPath(string transferId) => Path.Combine(TransferPath(transferId), "payload");
    private string StatePath(string transferId) => Path.Combine(TransferPath(transferId), "state.json");
    private static long SafeLength(string path) { try { return new FileInfo(path).Length; } catch { return 0; } }
    private static string Sanitize(string value)
    {
        var invalid = Path.GetInvalidFileNameChars().ToHashSet();
        var clean = new string(value.Select(c => invalid.Contains(c) || c is '/' or '\\' ? '_' : c).ToArray()).Trim();
        return string.IsNullOrWhiteSpace(clean) ? "Track" : clean.Length > 120 ? clean[..120] : clean;
    }
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    [GeneratedRegex(@"download:\s*([0-9]+(?:\.[0-9]+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    private enum DirectAudioPhase { Queued, Downloading, Completed, Failed }
    private sealed class DirectAudioState
    {
        public string TransferId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public List<YouTubeMusicTrack> Tracks { get; set; } = new();
        public DirectAudioPhase Phase { get; set; }
        public int Attempts { get; set; }
        public double Progress { get; set; }
        public string? CurrentTrack { get; set; }
        public string? Error { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
