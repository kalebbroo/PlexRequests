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
    /// <summary>Build every valid TMDb episode-group profile for safe manifest comparison. Invalid/empty
    /// community groups are skipped independently so one bad group cannot suppress the usable choices.</summary>
    Task<List<SeriesEpisodeOrderProfileDto>> GetCandidateProfilesAsync(int tmdbId, string seriesTitle,
        CancellationToken ct = default);
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

    public async Task<List<SeriesEpisodeOrderProfileDto>> GetCandidateProfilesAsync(int tmdbId,
        string seriesTitle, CancellationToken ct = default)
    {
        var summaries = await GetGroupsAsync(tmdbId, ct);
        var profiles = new List<SeriesEpisodeOrderProfileDto>();
        foreach (var summary in summaries.Where(group => group.EpisodeCount > 0).Take(24))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                profiles.Add((await BuildPreviewAsync(tmdbId, seriesTitle, summary.Id, ct)).Profile);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex,
                    "Skipping invalid TMDb episode group {GroupId} ({GroupName}) for series {TmdbId}",
                    summary.Id, summary.Name, tmdbId);
            }
        }

        return profiles
            .DistinctBy(profile => profile.SourceEpisodeGroupId, StringComparer.Ordinal)
            .ToList();
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
        // Most TMDb groups use one-based or semantic ordinals (sometimes with deliberate gaps), while
        // some community groups are zero-based. If zero appears, shift the entire set together so group 0
        // becomes source folder 1 and group 1 becomes source folder 2; shifting only the first group would
        // create a duplicate and shifting every group unconditionally would break semantic orders.
        var sourceGroupOffset = details.Type != TvGroupType.Absolute && groups.Any(group => group.Order == 0)
            ? 1 : 0;
        var lines = new List<string>();
        var customEpisodes = new List<CustomEpisodeMetadataDto>();
        var skippedSpecials = 0;
        if (details.Type == TvGroupType.Absolute)
        {
            var absolute = groups.SelectMany(group => (group.Episodes ?? []).OrderBy(x => x.Order)).ToList();
            foreach (var episode in absolute)
            {
                customEpisodes.Add(CustomEpisode(0, episode.Order + 1, episode));
                if (episode.SeasonNumber == 0)
                {
                    skippedSpecials++;
                    continue;
                }
                lines.Add($"A{episode.Order + 1} -> S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}");
            }
        }
        else
        {
            foreach (var group in groups)
            foreach (var episode in (group.Episodes ?? []).OrderBy(x => x.Order))
            {
                lines.Add($"S{group.Order + sourceGroupOffset:D2}E{episode.Order + 1:D2} -> S{episode.SeasonNumber:D2}E{episode.EpisodeNumber:D2}");
                customEpisodes.Add(CustomEpisode(group.Order + sourceGroupOffset, episode.Order + 1, episode));
            }
        }

        var profile = new SeriesEpisodeOrderProfileDto
        {
            TmdbId = tmdbId,
            SeriesTitle = string.IsNullOrWhiteSpace(seriesTitle) ? $"TMDb {tmdbId}" : seriesTitle.Trim(),
            SourceOrder = sourceOrder,
            SourceEpisodeGroupId = details.Id,
            SourceEpisodeGroupName = string.IsNullOrWhiteSpace(details.Name) ? "Unnamed group" : details.Name,
            ImportedAt = importedAt ?? DateTime.UtcNow,
            SourceGroups = details.Type == TvGroupType.Absolute
                ? []
                : groups.Where(group => group.Order + sourceGroupOffset > 0 && !string.IsNullOrWhiteSpace(group.Name))
                    .Select(group => new EpisodeOrderSourceGroupDto
                    {
                        SourceSeason = group.Order + sourceGroupOffset,
                        Name = group.Name.Trim()
                    })
                    .ToList(),
            CustomSeasons = customEpisodes.Select(row => row.Season).Distinct().Order()
                .Select(season => new CustomSeasonMetadataDto
                {
                    Season = season,
                    Name = season == 0 ? "Specials" : $"Season {season}"
                }).ToList(),
            CustomEpisodes = customEpisodes,
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

    private static CustomEpisodeMetadataDto CustomEpisode(int sourceSeason, int sourceEpisode,
        TvGroupEpisode episode) => new()
    {
        SourceSeason = sourceSeason,
        SourceEpisode = sourceEpisode,
        Season = episode.SeasonNumber,
        Episode = episode.EpisodeNumber,
        Title = episode.Name ?? string.Empty,
        OriginallyAvailableAt = episode.AirDate,
        ContentKind = episode.SeasonNumber == 0 ? "Special" : "Episode",
        // Provider specials include recaps, promos, and unrelated shorts. Show them in the builder, but
        // require an explicit opt-in before they become whole-series download targets.
        IncludeInMonitoring = episode.SeasonNumber > 0
    };

    private TmdbMetadataProvider Provider() =>
        providers.GetProvider(MetadataProviderType.TMDb) as TmdbMetadataProvider
        ?? throw new InvalidOperationException("The configured TMDb provider is unavailable.");
}
