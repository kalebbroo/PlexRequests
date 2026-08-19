using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>
/// Durable, Discord-agnostic lifecycle event store consumed by the external PlexBox bridge. The app owns
/// request facts; the bot remains responsible for Discord rendering, delivery and interaction logic.
/// </summary>
public interface IBridgeOutboxService
{
    int RetentionDays { get; }

    Task EnqueueAsync(MediaRequestDto request, BridgeEventType type, string? detail = null,
        CancellationToken cancellationToken = default);

    Task<BridgeEventBatchDto> ReadBatchAsync(long cursor, int take,
        CancellationToken cancellationToken = default);

    Task<BridgeEventAckResult> AcknowledgeAsync(long cursor,
        CancellationToken cancellationToken = default);
}
