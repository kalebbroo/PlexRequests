using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IDirectVideoSettingsService
{
    Task<DirectVideoSettingsDto> GetAsync(CancellationToken cancellationToken = default);
    /// <summary>Validates channel ids and refuses to enable the source without a configured API key.</summary>
    Task UpdateAsync(DirectVideoSettingsDto settings, CancellationToken cancellationToken = default);
}
