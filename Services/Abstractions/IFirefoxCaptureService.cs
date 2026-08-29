using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IFirefoxCaptureService
{
    Task<FirefoxCapturePairingDto> CreatePairingAsync(int indexerId, CancellationToken cancellationToken = default);
    Task<FirefoxCapturePairResponseDto?> RedeemPairingAsync(
        FirefoxCapturePairRequestDto request,
        CancellationToken cancellationToken = default);
    Task<FirefoxCaptureConnectionDto?> GetConnectionAsync(
        string token,
        CancellationToken cancellationToken = default);
    Task<FirefoxCaptureConnectionDto?> RecordHeartbeatAsync(
        string token,
        FirefoxCaptureHeartbeatDto heartbeat,
        CancellationToken cancellationToken = default);
    Task<CatalogHydrationPageDto?> GetPendingDetailsAsync(
        string token,
        long afterId,
        int limit,
        CancellationToken cancellationToken = default);
    Task<FirefoxCaptureIngestResponseDto> IngestAsync(
        string token,
        FirefoxCaptureBatchDto batch,
        CancellationToken cancellationToken = default);
    Task<bool?> ReportHydrationFailureAsync(
        string token,
        FirefoxCaptureHydrationFailureDto failure,
        CancellationToken cancellationToken = default);
    Task<FirefoxCaptureAdminStatusDto> GetAdminStatusAsync(
        int indexerId,
        CancellationToken cancellationToken = default);
    Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default);
}
