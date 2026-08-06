using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public class SeasonAvailabilityEvaluator(AppDbContext db, IMediaMetadataProvider metadata) : ISeasonAvailabilityEvaluator
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;

    public async Task<string?> ResolveRatingKeyAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var ratingKey = await _db.PlexMappings.AsNoTracking()
            .Where(m => m.ExternalKey == $"tmdb:{tmdbShowId}")
            .Select(m => m.RatingKey).FirstOrDefaultAsync(ct);
        if (!string.IsNullOrEmpty(ratingKey)) return ratingKey;

        // Plex tagged this show with a different agent's id. The IMDb id we already cache for it is the
        // one alternative we can resolve today; a cached TVDB id would be the third and is the remaining
        // gap for shows Plex knows only by tvdb://.
        var imdbId = await _db.MediaMetadataCache.AsNoTracking()
            .Where(c => c.TmdbId == tmdbShowId && c.MediaType == MediaType.TvShow && c.ImdbId != null)
            .Select(c => c.ImdbId).FirstOrDefaultAsync(ct);
        if (string.IsNullOrWhiteSpace(imdbId)) return null;

        return await _db.PlexMappings.AsNoTracking()
            .Where(m => m.ExternalKey == $"imdb:{imdbId}")
            .Select(m => m.RatingKey).FirstOrDefaultAsync(ct);
    }

    public async Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var result = new Dictionary<int, HashSet<int>>();
        var ratingKey = await ResolveRatingKeyAsync(tmdbShowId, ct);
        if (string.IsNullOrEmpty(ratingKey)) return result;
        var seasons = await _db.PlexSeasonAvailability.Where(s => s.ShowRatingKey == ratingKey).ToListAsync(ct);
        foreach (var s in seasons)
            result[s.SeasonNumber] = s.AvailableEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
        return result;
    }

    public async Task<Dictionary<int, SeasonCompleteness>> EvaluateAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var onPlex = await GetPlexEpisodesAsync(tmdbShowId, ct);
        var detail = await _metadata.GetDetailsAsync(tmdbShowId, MediaType.TvShow);
        var result = new Dictionary<int, SeasonCompleteness>();
        var seasons = detail?.Seasons?.Where(s => s.SeasonNumber > 0) ?? Enumerable.Empty<Shared.DTOs.SeasonDto>();
        foreach (var s in seasons)
        {
            var plexSet = onPlex.TryGetValue(s.SeasonNumber, out var set) ? set : new HashSet<int>();
            var plexCount = plexSet.Count;
            var countUnknown = s.EpisodeCount <= 0;
            // Complete only when Plex has at least the number of episodes TMDB says exist. When TMDB doesn't
            // report a count we can't conclude anything — treating "one episode present" as a complete season
            // made ComputeMissingTvTargetsAsync drop the season from the job entirely, so the rest of it was
            // never fetched. Unknown counts stay incomplete and are handled as pack-only targets (no episode
            // list to fan out to), which is the honest answer.
            var complete = !countUnknown && plexCount >= s.EpisodeCount;
            var aired = !s.AirDate.HasValue || s.AirDate.Value.Date <= DateTime.UtcNow.Date;
            var missing = countUnknown
                ? new List<int>()
                : Enumerable.Range(1, s.EpisodeCount).Where(n => !plexSet.Contains(n)).ToList();
            result[s.SeasonNumber] = new SeasonCompleteness(s.SeasonNumber, plexCount, s.EpisodeCount, complete, aired, missing, countUnknown);
        }
        return result;
    }

    public async Task<List<int>> GetCompleteSeasonsAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var eval = await EvaluateAsync(tmdbShowId, ct);
        // Satisfaction query (drives "which seasons are already available"), so it uses the same lenient
        // reading of an unknown TMDB episode count as IsWholeSeriesSatisfiedAsync — strictness here would
        // leave season-scoped requests for those shows permanently unreconciled.
        return eval.Where(kv => kv.Value.Complete || (kv.Value.CountUnknown && kv.Value.PlexCount > 0))
            .Select(kv => kv.Key).OrderBy(n => n).ToList();
    }

    public async Task<bool> IsWholeSeriesSatisfiedAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var eval = await EvaluateAsync(tmdbShowId, ct);
        // No TMDB season metadata at all -> can't confirm completeness; don't claim satisfaction.
        if (eval.Count == 0) return false;
        // Only aired seasons must be complete; unaired future seasons are the monitor's job, not this check's.
        // A season whose episode count TMDB doesn't report can never be strictly Complete, so accept "has
        // episodes" for it here — otherwise such a request would sit un-Available forever and, because the
        // series monitor only looks at Available requests, would never be monitored either.
        return eval.Values.Where(s => s.Aired).All(s => s.Complete || (s.CountUnknown && s.PlexCount > 0));
    }
}
