using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IDirectVideoEpisodeResolver
{
    /// <summary>Freezes YouTube identities for the outstanding episodes of a TV job. Null means the source could not be
    /// consulted (disabled, unconfigured, or unreachable); the caller retries on a later claim and torrents still run.</summary>
    Task<DirectVideoAcquisitionContextDto?> ResolveAsync(FulfillmentJobDto job, CancellationToken cancellationToken = default);
}
