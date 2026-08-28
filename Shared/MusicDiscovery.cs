namespace PlexRequestsHosted.Shared.DTOs;

/// <summary>Provider-neutral model for the dedicated music discovery experience.</summary>
public sealed class MusicBrowseHubDto
{
    public string SourceLabel { get; set; } = "Music catalog";
    public List<MediaCardDto> NewReleases { get; set; } = new();
    public List<MediaCardDto> TrendingTracks { get; set; } = new();
    public List<MediaCardDto> PopularArtists { get; set; } = new();
    public List<MusicPlaylistSummaryDto> FeaturedPlaylists { get; set; } = new();
    public List<MusicCategoryDto> Categories { get; set; } = new();
}

/// <summary>A grouped music search response. Keeping kinds separate lets the Music page lead with an
/// exact artist match without burying albums and songs in one ambiguous poster grid.</summary>
public sealed class MusicCatalogSearchResultDto
{
    public string Query { get; set; } = string.Empty;
    public List<MediaCardDto> Artists { get; set; } = new();
    public List<MediaCardDto> Albums { get; set; } = new();
    public List<MediaCardDto> Tracks { get; set; } = new();
    public int TotalCount => Artists.Count + Albums.Count + Tracks.Count;

    public IEnumerable<MediaCardDto> AllItems() => Artists.Concat(Albums).Concat(Tracks);
}

public sealed class MusicCategoryDto
{
    public string Title { get; set; } = string.Empty;
    public string Group { get; set; } = "Moods & genres";
    /// <summary>Opaque provider token. It is passed back unchanged and never interpreted by the UI.</summary>
    public string Token { get; set; } = string.Empty;
}

public class MusicPlaylistSummaryDto
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string? Subtitle { get; set; }
    public string? ArtworkUrl { get; set; }
}

public sealed class MusicPlaylistDto : MusicPlaylistSummaryDto
{
    public string? Description { get; set; }
    public int? TrackCount { get; set; }
    public List<MediaCardDto> Tracks { get; set; } = new();
}
