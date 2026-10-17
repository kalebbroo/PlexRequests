using System.Collections.Concurrent;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Download;

/// <summary>Restart-safe single-video backend around yt-dlp for TV episodes. Each transfer owns a small atomic state
/// file and a deterministic payload directory, so an interrupted download resumes after a restart. Identity is not
/// decided here: the organizer proves the file belongs to the episode by the frozen video id in its filename.</summary>
public sealed partial class YouTubeVideoAcquisitionBackend : IAcquisitionBackend
{
    // yt-dlp reports success only after the stream is finalized. This floor rejects empty or truncated artifacts.
    private const long MinimumCompletedFileBytes = 64 * 1024;
    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        { ".mp4", ".mkv", ".webm" };
    private readonly DirectVideoOptions _options;
    private readonly ILogger<YouTubeVideoAcquisitionBackend> _logger;
    private readonly CancellationToken _stoppingToken;
    private readonly string _root;
    private readonly SemaphoreSlim _concurrency;
    private readonly ConcurrentDictionary<string, Task> _workers = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _cancellations = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _stateLocks = new(StringComparer.Ordinal);

    public YouTubeVideoAcquisitionBackend(IOptions<DirectVideoOptions> options, IHostApplicationLifetime lifetime,
        ILogger<YouTubeVideoAcquisitionBackend> logger)
    {
        _options = options.Value;
        _logger = logger;
        _stoppingToken = lifetime.ApplicationStopping;
        _root = Path.GetFullPath(_options.StatePath);
        _concurrency = new SemaphoreSlim(Math.Clamp(_options.MaxConcurrent, 1, 4));
        Directory.CreateDirectory(_root);
    }

    public AcquisitionProtocol Protocol => AcquisitionProtocol.DirectVideo;
    public AcquisitionBackendCapabilities Capabilities { get; } = new(false, false, false);

    public async Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct)
    {
        if (!_options.Enabled || request.Resource.Protocol != Protocol
            || !DirectVideoLocator.TryDecode(request.Resource.Locator, out var locator)
            || string.IsNullOrWhiteSpace(request.Resource.SourceId)) return null;

        var identity = $"{request.CorrelationId ?? Guid.NewGuid().ToString("N")}|{request.Resource.SourceId}";
        var transferId = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity))).ToLowerInvariant()[..32];
        var gate = StateLock(transferId);
        await gate.WaitAsync(ct);
        try
        {
            if (await ReadStateAsync(transferId, ct) is null)
            {
                Directory.CreateDirectory(PayloadPath(transferId));
                await WriteStateAsync(new DirectVideoState
                {
                    TransferId = transferId,
                    DisplayName = request.DisplayName,
                    SourceId = request.Resource.SourceId,
                    VideoId = locator.VideoId,
                    Season = locator.Season,
                    Episode = locator.Episode,
                    Phase = DirectVideoPhase.Queued,
                    UpdatedAt = DateTime.UtcNow
                }, ct);
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
        if (state.Phase is DirectVideoPhase.Queued or DirectVideoPhase.Downloading) StartWorker(transferId);

        var files = CompletedFiles(transferId);
        return new TransferStatus(
            state.Phase == DirectVideoPhase.Failed ? "Error" : state.Phase.ToString(),
            state.Phase == DirectVideoPhase.Completed ? 100 : Math.Clamp(state.Progress, 0, 99.9),
            state.DisplayName,
            files.Sum(SafeLength),
            PayloadPath(transferId),
            state.Phase == DirectVideoPhase.Completed,
            files.Select(x => Path.GetFileName(x)!).ToList(),
            ProviderStatus: state.Error);
    }

    public TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt,
        DateTime? progressChangedAt, DateTime now)
    {
        if (status is null) return new(TransferVerdict.Missing, "Direct-video transfer state is missing");
        if (status.IsFinished) return new(TransferVerdict.Import);
        if (status.State.Equals("Error", StringComparison.OrdinalIgnoreCase))
            return new(TransferVerdict.Fail, status.ProviderStatus ?? "Direct-video extraction failed", Blocklist: true);
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
            _logger.LogError(ex, "Direct-video transfer {TransferId} crashed", transferId);
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
                if (state is null || state.Phase is DirectVideoPhase.Completed or DirectVideoPhase.Failed) return;
                state.Attempts = attempt;
                state.Phase = DirectVideoPhase.Downloading;
                state.Error = null;
                await WriteStateLockedAsync(state, ct);

                try
                {
                    if (FindVideoFile(transferId, state.VideoId) is null)
                        await DownloadVideoAsync(transferId, state, ct);
                    if (FindVideoFile(transferId, state.VideoId) is null)
                        throw new InvalidOperationException($"yt-dlp exited without producing a video file for {state.VideoId}");

                    state.Phase = DirectVideoPhase.Completed;
                    state.Progress = 100;
                    state.Error = null;
                    await WriteStateLockedAsync(state, ct);
                    _logger.LogInformation("Direct-video transfer {TransferId} completed ({VideoId})", transferId, state.VideoId);
                    return;
                }
                catch (Exception ex) when (ex is not OperationCanceledException && attempt < maxAttempts)
                {
                    state.Error = ex.Message;
                    await WriteStateLockedAsync(state, ct);
                    _logger.LogWarning(ex, "Direct-video transfer {TransferId} attempt {Attempt}/{Max} failed; retrying",
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

    private async Task DownloadVideoAsync(string transferId, DirectVideoState state, CancellationToken ct)
    {
        var output = Path.Combine(PayloadPath(transferId), $"{Sanitize(state.DisplayName)} [{state.VideoId}].%(ext)s");
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
            "--format", _options.Format, "--output", output,
            $"https://www.youtube.com/watch?v={state.VideoId}"
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
            {
                state.Progress = Math.Clamp(percent, 0, 99.9);
                await WriteStateLockedAsync(state, ct);
            }
        }, ct);
        var stderr = ReadLinesAsync(process.StandardError, line =>
        {
            if (!string.IsNullOrWhiteSpace(line)) lastError = line.Trim();
            return Task.CompletedTask;
        }, ct);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(Math.Clamp(_options.TimeoutMinutes, 2, 180)));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch (OperationCanceledException)
        {
            TryKill(process);
            if (!ct.IsCancellationRequested)
                throw new TimeoutException($"Video {state.VideoId} exceeded the configured timeout");
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

    private async Task MarkFailedAsync(string transferId, string error, CancellationToken ct)
    {
        var state = await ReadStateAsync(transferId, ct);
        if (state is null) return;
        state.Phase = DirectVideoPhase.Failed;
        state.Error = error.Length > 2000 ? error[..2000] : error;
        await WriteStateLockedAsync(state, ct);
    }

    private List<string> CompletedFiles(string transferId) => Directory.Exists(PayloadPath(transferId))
        ? Directory.EnumerateFiles(PayloadPath(transferId), "*", SearchOption.TopDirectoryOnly)
            .Where(x => VideoExtensions.Contains(Path.GetExtension(x)) && SafeLength(x) >= MinimumCompletedFileBytes)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList()
        : new();

    private string? FindVideoFile(string transferId, string videoId) => CompletedFiles(transferId)
        .FirstOrDefault(x => Path.GetFileName(x).Contains($"[{videoId}]", StringComparison.Ordinal));

    private async Task<DirectVideoState?> ReadStateAsync(string transferId, CancellationToken ct)
    {
        var path = StatePath(transferId);
        if (!File.Exists(path)) return null;
        try
        {
            await using var stream = File.OpenRead(path);
            return await JsonSerializer.DeserializeAsync<DirectVideoState>(stream, cancellationToken: ct);
        }
        catch (JsonException ex)
        {
            _logger.LogWarning(ex, "Direct-video state {TransferId} is corrupt", transferId);
            return null;
        }
    }

    private async Task WriteStateLockedAsync(DirectVideoState state, CancellationToken ct)
    {
        var gate = StateLock(state.TransferId);
        await gate.WaitAsync(ct);
        try { await WriteStateAsync(state, ct); }
        finally { gate.Release(); }
    }

    private async Task WriteStateAsync(DirectVideoState state, CancellationToken ct)
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
        return string.IsNullOrWhiteSpace(clean) ? "Episode" : clean.Length > 160 ? clean[..160] : clean;
    }
    private static void TryKill(Process process) { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch { } }

    [GeneratedRegex(@"download:\s*([0-9]+(?:\.[0-9]+)?)%", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressRegex();

    private enum DirectVideoPhase { Queued, Downloading, Completed, Failed }
    private sealed class DirectVideoState
    {
        public string TransferId { get; set; } = string.Empty;
        public string DisplayName { get; set; } = string.Empty;
        public string SourceId { get; set; } = string.Empty;
        public string VideoId { get; set; } = string.Empty;
        public int Season { get; set; }
        public int Episode { get; set; }
        public DirectVideoPhase Phase { get; set; }
        public int Attempts { get; set; }
        public double Progress { get; set; }
        public string? Error { get; set; }
        public DateTime UpdatedAt { get; set; }
    }
}
