using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Abstractions;

/// <summary>Resolves a canonical catalog item to an optional, complete direct-audio manifest. A null result
/// is a normal outcome: torrent/Usenet sources remain available and a later queue claim can retry.</summary>
public interface IMusicDirectAcquisitionResolver
{
    Task<DirectAudioAcquisitionContextDto?> ResolveAsync(MediaRef canonical, MediaDetailDto detail,
        CancellationToken ct = default);
}
