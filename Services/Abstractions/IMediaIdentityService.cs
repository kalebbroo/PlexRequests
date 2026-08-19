using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaIdentityService
{
    Task<MediaIdentityEntity> ResolveAsync(MediaRef mediaRef, CancellationToken cancellationToken = default);
    Task<MediaRef?> GetRefAsync(int mediaIdentityId, CancellationToken cancellationToken = default);
    Task AddAliasAsync(int mediaIdentityId, MediaRef alias, CancellationToken cancellationToken = default);
}
