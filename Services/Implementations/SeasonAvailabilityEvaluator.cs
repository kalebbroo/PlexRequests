using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public class SeasonAvailabilityEvaluator(AppDbContext db, IMediaMetadataProvider metadata) : ISeasonAvailabilityEvaluator
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;

    public async Task<Dictionary<int, HashSet<int>>> GetPlexEpisodesAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var result = new Dictionary<int, HashSet<int>>();
        var ratingKey = await _db.PlexMappings.Where(m => m.ExternalKey == $"tmdb:{tmdbShowId}").Select(m => m.RatingKey).FirstOrDefaultAsync(ct);
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
            // Complete when Plex has >= the TMDB episode count (or, if TMDB count unknown, any episodes at all).
            var complete = plexCount > 0 && (s.EpisodeCount <= 0 || plexCount >= s.EpisodeCount);
            var aired = !s.AirDate.HasValue || s.AirDate.Value.Date <= DateTime.UtcNow.Date;
            var missing = s.EpisodeCount > 0
                ? Enumerable.Range(1, s.EpisodeCount).Where(n => !plexSet.Contains(n)).ToList()
                : new List<int>();
            result[s.SeasonNumber] = new SeasonCompleteness(s.SeasonNumber, plexCount, s.EpisodeCount, complete, aired, missing);
        }
        return result;
    }

    public async Task<List<int>> GetCompleteSeasonsAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var eval = await EvaluateAsync(tmdbShowId, ct);
        return eval.Where(kv => kv.Value.Complete).Select(kv => kv.Key).OrderBy(n => n).ToList();
    }

    public async Task<bool> IsWholeSeriesSatisfiedAsync(int tmdbShowId, CancellationToken ct = default)
    {
        var eval = await EvaluateAsync(tmdbShowId, ct);
        // No TMDB season metadata at all -> can't confirm completeness; don't claim satisfaction.
        if (eval.Count == 0) return false;
        // Only aired seasons must be complete; unaired future seasons are the monitor's job, not this check's.
        return eval.Values.Where(s => s.Aired).All(s => s.Complete);
    }
}
