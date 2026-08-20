using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Download;

/// <summary>What a transfer backend can truthfully do. Callers use this instead of assuming every
/// protocol has torrent-only concepts such as file selection or seeding.</summary>
public sealed record AcquisitionBackendCapabilities(
    bool SupportsFileSelection,
    bool SupportsPeerTelemetry,
    bool CanKeepSourceDataAfterRemoval);

/// <summary>Protocol-neutral transfer status. Peer and tracker fields are optional diagnostics supplied
/// by torrent backends; direct and Usenet backends do not have to invent values for them.</summary>
public sealed record TransferStatus(
    string State,
    double Progress,
    string Name,
    long TotalSizeBytes,
    string? SavePath,
    bool IsFinished,
    IReadOnlyList<string> Files,
    double DownloadRate = 0,
    int? Seeds = null,
    int? Peers = null,
    long? Eta = null,
    string? ProviderStatus = null);

public sealed record AcquisitionRequest(
    AcquisitionResource Resource,
    string? Label,
    string DisplayName);

public enum TransferVerdict { Wait, Import, Fail, Missing }

public sealed record TransferHealthDecision(
    TransferVerdict Verdict,
    string? Reason = null,
    bool Blocklist = false);

/// <summary>A modular byte-transfer backend. Discovery and ranking never depend on a concrete downloader;
/// the selected candidate is routed here solely by its declared protocol.</summary>
public interface IAcquisitionBackend
{
    AcquisitionProtocol Protocol { get; }
    AcquisitionBackendCapabilities Capabilities { get; }

    Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct);
    Task<TransferStatus?> GetStatusAsync(string transferId, CancellationToken ct);
    TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt, DateTime? progressChangedAt, DateTime now);
    Task<bool> RemoveAsync(string transferId, bool removeData, CancellationToken ct);
    Task<bool> SetWantedFilesAsync(string transferId, IReadOnlyList<bool> keep, CancellationToken ct);
}

public interface IAcquisitionBackendRegistry
{
    bool TryGet(AcquisitionProtocol protocol, out IAcquisitionBackend backend);
    IReadOnlyList<(AcquisitionProtocol Protocol, AcquisitionBackendCapabilities Capabilities)> Capabilities { get; }
}

public sealed class AcquisitionBackendRegistry : IAcquisitionBackendRegistry
{
    private readonly IReadOnlyDictionary<AcquisitionProtocol, IAcquisitionBackend> _backends;

    public AcquisitionBackendRegistry(IEnumerable<IAcquisitionBackend> backends)
    {
        var materialized = backends.ToList();
        var duplicate = materialized.GroupBy(x => x.Protocol).FirstOrDefault(x => x.Count() > 1);
        if (duplicate is not null)
            throw new InvalidOperationException($"More than one acquisition backend is registered for {duplicate.Key}");

        _backends = materialized.ToDictionary(x => x.Protocol);
        Capabilities = materialized
            .OrderBy(x => x.Protocol)
            .Select(x => (x.Protocol, x.Capabilities))
            .ToList();
    }

    public IReadOnlyList<(AcquisitionProtocol Protocol, AcquisitionBackendCapabilities Capabilities)> Capabilities { get; }

    public bool TryGet(AcquisitionProtocol protocol, out IAcquisitionBackend backend) =>
        _backends.TryGetValue(protocol, out backend!);
}

/// <summary>Compatibility adapter around the existing Deluge client. Deluge remains isolated behind its
/// torrent protocol instead of leaking magnets and torrent ids through the fulfillment pipeline.</summary>
public sealed class TorrentAcquisitionBackend(IDownloadClient client) : IAcquisitionBackend
{
    public AcquisitionProtocol Protocol => AcquisitionProtocol.Torrent;
    public AcquisitionBackendCapabilities Capabilities { get; } = new(
        SupportsFileSelection: true,
        SupportsPeerTelemetry: true,
        CanKeepSourceDataAfterRemoval: true);

    public Task<string?> EnqueueAsync(AcquisitionRequest request, CancellationToken ct)
    {
        if (request.Resource.Protocol != Protocol)
            throw new ArgumentException($"Torrent backend cannot enqueue {request.Resource.Protocol}", nameof(request));
        return client.AddMagnetAsync(request.Resource.Locator, request.Label, ct);
    }

    public async Task<TransferStatus?> GetStatusAsync(string transferId, CancellationToken ct)
    {
        var status = await client.GetStatusAsync(transferId, ct);
        return status is null ? null : new TransferStatus(
            status.State,
            status.Progress,
            status.Name,
            status.TotalSizeBytes,
            status.SavePath,
            status.IsFinished,
            status.Files,
            status.DownloadRate,
            status.Seeds,
            status.Peers,
            status.Eta > 0 ? status.Eta : null,
            status.TrackerStatus);
    }

    public Task<bool> RemoveAsync(string transferId, bool removeData, CancellationToken ct) =>
        client.RemoveAsync(transferId, removeData, ct);

    public Task<bool> SetWantedFilesAsync(string transferId, IReadOnlyList<bool> keep, CancellationToken ct) =>
        client.SetWantedFilesAsync(transferId, keep, ct);

    public TransferHealthDecision EvaluateHealth(TransferStatus? status, DateTime addedAt,
        DateTime? progressChangedAt, DateTime now)
    {
        var observation = status is null
            ? new TorrentObservation { PresentInClient = false }
            : new TorrentObservation
            {
                PresentInClient = true,
                ClientState = status.State ?? string.Empty,
                Progress = status.Progress,
                Seeds = status.Seeds ?? 0,
                Peers = status.Peers ?? 0,
                IsFinished = status.IsFinished,
                TrackerStatus = status.ProviderStatus,
                HasMetadata = status.TotalSizeBytes > 0
            };
        var decision = TorrentHealth.Decide(observation, addedAt, progressChangedAt, now);
        return new TransferHealthDecision(decision.Verdict switch
        {
            TorrentVerdict.Import => TransferVerdict.Import,
            TorrentVerdict.Fail => TransferVerdict.Fail,
            TorrentVerdict.Gone => TransferVerdict.Missing,
            _ => TransferVerdict.Wait
        }, decision.Reason, decision.Blocklist);
    }
}
