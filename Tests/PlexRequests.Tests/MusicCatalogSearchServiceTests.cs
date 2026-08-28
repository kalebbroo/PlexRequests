using Microsoft.Extensions.Logging.Abstractions;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using Xunit;

namespace PlexRequests.Tests;

public sealed class MusicCatalogSearchServiceTests
{
    [Fact]
    public async Task AllSearch_GroupsRealKindsAndRanksExactMatchesFirst()
    {
        var provider = new RecordingProvider();
        var service = Create(provider);

        var result = await service.SearchAsync("Daft Punk");

        Assert.Equal(new MediaKind?[] { MediaKind.Artist, MediaKind.Album, MediaKind.Track },
            provider.Queries.Select(x => x.Kind));
        Assert.Equal("Daft Punk", Assert.Single(result.Artists).Title);
        Assert.Equal("Daft Punk", result.Albums[0].Subtitle);
        Assert.Equal(3, result.TotalCount);
        Assert.All(result.AllItems(), item => Assert.True(item.ResolveMediaRef().IsValid));
    }

    [Fact]
    public async Task KindSearch_CallsOnlyTheSelectedCatalogEntity()
    {
        var provider = new RecordingProvider();
        var result = await Create(provider).SearchAsync("Discovery", MediaKind.Album);

        Assert.Equal(MediaKind.Album, Assert.Single(provider.Queries).Kind);
        Assert.Empty(result.Artists);
        Assert.NotEmpty(result.Albums);
        Assert.Empty(result.Tracks);
    }

    [Fact]
    public async Task DisabledCatalog_DoesNotContactMetadataProvider()
    {
        var provider = new RecordingProvider();
        var service = new MusicCatalogSearchService(provider, new Settings(false),
            NullLogger<MusicCatalogSearchService>.Instance);

        var result = await service.SearchAsync("Daft Punk");

        Assert.Equal(0, result.TotalCount);
        Assert.Empty(provider.Queries);
    }

    private static MusicCatalogSearchService Create(RecordingProvider provider) => new(provider,
        new Settings(true), NullLogger<MusicCatalogSearchService>.Instance);

    private sealed class Settings(bool enabled) : IMusicSettingsService
    {
        public Task<MusicSettingsDto> GetAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(new MusicSettingsDto { CatalogEnabled = enabled });
        public Task UpdateAsync(MusicSettingsDto settings, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class RecordingProvider : IMediaMetadataProvider
    {
        public List<MediaSearchQuery> Queries { get; } = new();

        public Task<List<MediaCardDto>> SearchAsync(MediaSearchQuery query)
        {
            Queries.Add(query);
            var kind = query.Kind ?? MediaKind.Album;
            var item = kind switch
            {
                MediaKind.Artist => Card("Daft Punk", null, kind),
                MediaKind.Track => Card("One More Time", "Daft Punk", kind),
                _ => Card("Discovery", "Daft Punk", kind)
            };
            // Invalid and cross-kind provider output must not leak into the grouped UI.
            return Task.FromResult(new List<MediaCardDto>
            {
                item,
                Card("Wrong kind", null, kind == MediaKind.Track ? MediaKind.Album : MediaKind.Track),
                new() { Title = "Invalid", MediaType = MediaType.Music }
            });
        }

        private static MediaCardDto Card(string title, string? subtitle, MediaKind kind)
        {
            var id = $"{kind.ToString().ToLowerInvariant()}-{title.Replace(' ', '-').ToLowerInvariant()}";
            return new MediaCardDto
            {
                Title = title,
                Subtitle = subtitle,
                MediaType = MediaType.Music,
                ExternalSource = "test",
                ExternalId = id,
                MediaRef = MediaRef.FromExternal("test", id, MediaType.Music, kind)
            };
        }

        public Task<List<MediaCardDto>> SearchAsync(string query, MediaType? mediaType = null,
            int page = 1, int pageSize = 20) => Task.FromResult(new List<MediaCardDto>());
        public Task<MediaDetailDto?> GetDetailsAsync(int mediaId, MediaType mediaType) =>
            Task.FromResult<MediaDetailDto?>(null);
        public Task<List<MediaCardDto>> GetRecentlyAddedAsync(int count = 10) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<List<MediaCardDto>> GetLibraryAsync(MediaType mediaType, int page = 1, int pageSize = 20) =>
            Task.FromResult(new List<MediaCardDto>());
        public Task<string?> GetImdbIdAsync(int mediaId, MediaType mediaType) => Task.FromResult<string?>(null);
    }
}
