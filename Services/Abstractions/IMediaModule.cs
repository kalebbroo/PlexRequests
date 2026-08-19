using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Abstractions;

public interface IMediaModule
{
    MediaModuleDescriptor Descriptor { get; }
}

public interface IMediaModuleRegistry
{
    IReadOnlyList<MediaModuleDescriptor> All { get; }
    IReadOnlyList<MediaModuleDescriptor> Enabled { get; }
    MediaModuleDescriptor Get(MediaType mediaType);
    bool IsEnabled(MediaType mediaType);
}
