using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMusicSettingsService
{
    Task<MusicSettingsDto> GetAsync(CancellationToken cancellationToken = default);
    Task UpdateAsync(MusicSettingsDto settings, CancellationToken cancellationToken = default);
}
