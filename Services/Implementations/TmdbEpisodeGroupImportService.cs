using PlexRequestsHosted.Services.MetadataProviders;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using TMDbLib.Objects.TvShows;

namespace PlexRequestsHosted.Services.Implementations;

public interface ITmdbEpisodeGroupImportService
{
    Task<List<MediaCardDto>> SearchSeriesAsync(string query, CancellationToken ct = default);
    Task<List<TmdbEpisodeGroupSummaryDto>> GetGroupsAsync(int tmdbId, CancellationToken ct = default);
    Task<EpisodeGroupImportPreviewDto> BuildPreviewAsync(int tmdbId, string seriesTitle, string groupId,
        CancellationToken ct = default);
}

/// <summary>Turns TMDb's zero-based episode-group ordering into the explicit, validated mapping contract
/// consumed by fulfillment jobs. The TMDb provider is resolved lazily so a server without TMDb credentials
/// can still render the admin page and report a useful error only when import is requested.</summary>
public sealed class TmdbEpisodeGroupImportService(
    IMetadataProviderFactory providers,
    ILogger<TmdbEpisodeGroupImportService> logger) : ITmdbEpisodeGroupImportService
{
    public async Task<List<MediaCardDto>> SearchSeriesAsync(string query, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Trim().Length < 2) return [];
        ct.ThrowIfCancellationRequested();
        var results = await Provider().SearchAsync(query.Trim(), MediaType.TvShow, page: 1, pageSize: 12);
        ct.ThrowIfCancellationRequested();
        return results.Where(x => x.MediaType == MediaType.TvShow && x.Id > 0).Take(12).ToList();
    }

    public async Task<List<TmdbEpisodeGroupSummaryDto>> GetGroupsAsync(int tmdbId,
        CancellationToken ct = default)
    {
        if (tmdbId <= 0) throw new ArgumentException("A valid TMDb series ID is required.", nameof(tmdbId));
        try
        {
            return await Provider().GetEpisodeGroupSummariesAsync(tmdbId, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "TMDb episode-group discovery failed for series {TmdbId}", tmdbId);
            throw new InvalidOperationException("TMDb episode groups could not be loaded. Verify the TMDb credentials and series ID.", ex);
        }
    }

    public async Task<EpisodeGroupImportPreviewDto> BuildPreviewAsync(int tmdbId, string seriesTitle,
        string groupId, CancellationToken ct = default)
    {
        if (tmdbId <= 0) throw new ArgumentException("A valid TMDb series ID is required.", nameof(tmdbId));
        if (string.IsNullOrWhiteSpace(groupId)) throw new ArgumentException("Select a TMDb episode group.", nameof(groupId));
        try
        {
            var details = await Provider().GetEpisodeGroupDetailsAsync(groupId.Trim(), ct)
                ?? throw new InvalidOperationException("TMDb returned no details for that episode group.");
            return BuildPreview(tmdbId, seriesTitle, details);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not ArgumentException)
        {
            logger.LogWarning(ex, "TMDb episode-group import failed for series {TmdbId}, group {GroupId}",
                tmdbId, groupId);
            throw new InvalidOperationException($"TMDb episode group import failed: {ex.Message}", ex);
        }
    }

    internal static EpisodeGroupImportPreviewDto BuildPreview(int tmdbId, string seriesTitle,
        TvGroupCollection details, DateTime? importedAt = null)
    {
        var groups = (details.Groups ?? []).OrderBy(x => x.Order).ToList();
        var allEpisodes = groups.SelectMany(x => x.Episodes ?? []).ToList();
        if (allEpisodes.Count == 0) throw new ArgumentException("The selected TMDb episode group contains no episodes.");
        if (allEpisodes.Any(x => x.ShowId != tmdbId))
            throw new ArgumentException("The selected episode group belongs to a different TMDb series.");

        var sourceOrder = details.Type switch
        {
            TvGroupType.Absolute => EpisodeOrderType.Absolute,
            TvGroupType.DVD => EpisodeOrderType.Dvd,
            TvGroupType.Digital => EpisodeOrderType.Digital,
            _ => EpisodeOrderType.Custom
        };
        var lines = new List<string>();
        var skippedSpecials = 0;
        if (details.Type == TvGroupType.Absolute)
        {
            var absolute = groups.SelectMany(group => (group.Episodes ?? []).OrderBy(x => x.Order))
                .Where(episode =>
                {
                    if (episode.SeasonNumber != 0) return true;
                    skippedSpecials++;
                    return false;
                })
                .ToList();
            foreach (var episode in absolute)
                lines.Add($"A{episode.Order + 1} -> S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}");
        }
        else
        {
            foreach (var group in groups)
            foreach (var episode in (group.Episodes ?? []).OrderBy(x => x.Order))
                lines.Add($"S{group.Order:D2}E{episode.Order + 1:D2} -> S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}");
        }

        var profile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = tmdbId,
            SeriesTitle = string.IsNullOrWhiteSpace(seriesTitle) ? $"TMDb {tmdbId}" : seriesTitle.Trim(),
            SourceOrder = sourceOrder,
            SourceEpisodeGroupId = details.Id,
            SourceEpisodeGroupName = string.IsNullOrWhiteSpace(details.Name) ? "Unnamed group" : details.Name,
            ImportedAt = importedAt ?? DateTime.UtcNow,
            MappingsText = string.Join('\n', lines),
            Enabled = true
        };
        if (!EpisodeOrderMapping.TryParse(profile, out var mappings, out var error))
            throw new ArgumentException($"TMDb produced an unsafe episode map: {error}");
        profile.MappingsText = EpisodeOrderMapping.Normalize(profile);

        return new EpisodeGroupImportPreviewDto
        {
            Profile = profile,
            SourceEpisodeCount = allEpisodes.Count,
            MappedEpisodeCount = mappings.Count,
            SkippedSpecialCount = skippedSpecials,
            Warning = skippedSpecials > 0
                ? $"Skipped {skippedSpecials} absolute-order special(s); specials require an explicit season-based mapping."
                : null
        };
    }

    private TmdbMetadataProvider Provider() =>
        providers.GetProvider(MetadataProviderType.TMDb) as TmdbMetadataProvider
        ?? throw new InvalidOperationException("The configured TMDb provider is unavailable.");
}
