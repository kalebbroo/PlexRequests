using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IRecommendedFeedService
{
    /// <summary>Accept a batch of titles from the downloader and resolve them to real metadata.
    /// Returns how many resolved.</summary>
    Task<int> IngestAsync(List<RecommendedFeedItemDto> items);

    /// <summary>The cached row for the home page. Empty when there's nothing fresh to show.</summary>
    Task<List<MediaCardDto>> GetAsync(int count = 20);
}

/// <summary>
/// Turns raw release titles from the downloader into requestable cards.
///
/// The resolution step is what makes this worth having: a release name is not a title anyone can act on —
/// it has no poster, no id and no way to request it. Anything that can't be matched to a real TMDB entry is
/// dropped rather than shown, because a card with no artwork and a dead Request button is worse than one
/// fewer card.
/// </summary>
public class RecommendedFeedService(
    AppDbContext db,
    IMediaMetadataProvider metadata,
    ILogger<RecommendedFeedService> logger) : IRecommendedFeedService
{
    /// <summary>Beyond this, the feed is treated as dead and the row hides rather than showing stale picks.</summary>
    private static readonly TimeSpan MaxAge = TimeSpan.FromHours(24);

    public async Task<int> IngestAsync(List<RecommendedFeedItemDto> items)
    {
        if (items.Count == 0) return 0;
        var now = DateTime.UtcNow;

        // Titles already resolved keep their metadata — this is the cache that stops a TMDB lookup per
        // title per hour.
        var existing = await db.RecommendedItems.ToDictionaryAsync(r => (r.SourceTitle.ToLowerInvariant(), r.MediaType));
        int resolved = 0;

        foreach (var item in items)
        {
            var key = (item.Title.ToLowerInvariant(), item.MediaType);
            if (existing.TryGetValue(key, out var row))
            {
                row.Rank = item.Rank;
                row.SeenAt = now;
                row.SourceIndexerId = item.IndexerId;
                resolved++;
                continue;
            }

            var match = await ResolveAsync(item);
            if (match is null) continue;

            // The unique (TmdbId, MediaType) index means two release names for the same title collapse.
            var already = await db.RecommendedItems.FirstOrDefaultAsync(r => r.TmdbId == match.Id && r.MediaType == item.MediaType);
            if (already is not null)
            {
                already.Rank = Math.Min(already.Rank, item.Rank);
                already.SeenAt = now;
                resolved++;
                continue;
            }

            var entity = new RecommendedItemEntity
            {
                SourceTitle = item.Title,
                SourceYear = item.Year,
                MediaType = item.MediaType,
                TmdbId = match.Id,
                Title = match.Title,
                PosterUrl = match.PosterUrl,
                Year = match.Year,
                SourceIndexerId = item.IndexerId,
                Rank = item.Rank,
                SeenAt = now
            };
            db.RecommendedItems.Add(entity);
            existing[key] = entity;
            resolved++;
        }

        // Drop anything the feed has stopped reporting, so the row reflects what's current rather than
        // accumulating forever.
        var cutoff = now - MaxAge;
        await db.RecommendedItems.Where(r => r.SeenAt < cutoff).ExecuteDeleteAsync();

        await db.SaveChangesAsync();
        logger.LogInformation("Recommended feed: {Resolved} of {Total} title(s) resolved to metadata", resolved, items.Count);
        return resolved;
    }

    public async Task<List<MediaCardDto>> GetAsync(int count = 20)
    {
        var cutoff = DateTime.UtcNow - MaxAge;
        return await db.RecommendedItems.AsNoTracking()
            .Where(r => r.SeenAt >= cutoff && r.PosterUrl != null)
            .OrderBy(r => r.Rank)
            .Take(Math.Clamp(count, 1, 60))
            .Select(r => new MediaCardDto
            {
                Id = r.TmdbId,
                TmdbId = r.TmdbId,
                MediaType = r.MediaType,
                Title = r.Title,
                PosterUrl = r.PosterUrl,
                Year = r.Year
            })
            .ToListAsync();
    }

    /// <summary>Match a parsed release title to a real TMDB entry, preferring an exact title + year hit.</summary>
    private async Task<MediaCardDto?> ResolveAsync(RecommendedFeedItemDto item)
    {
        try
        {
            var results = await metadata.SearchAsync(item.Title, item.MediaType, 1, 5);
            if (results.Count == 0) return null;

            // A release name's year is the strongest disambiguator available (three films called "Dune").
            if (item.Year is int year)
            {
                var exact = results.FirstOrDefault(r => r.Year == year)
                            ?? results.FirstOrDefault(r => r.Year is int y && Math.Abs(y - year) <= 1);
                if (exact is not null) return exact;
            }

            var best = results[0];
            // Guard against a loose match: the top hit must at least start with what we searched for,
            // otherwise a garbled release name silently recommends an unrelated title.
            return best.Title.StartsWith(item.Title, StringComparison.OrdinalIgnoreCase)
                   || item.Title.StartsWith(best.Title, StringComparison.OrdinalIgnoreCase)
                ? best : null;
        }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "Could not resolve recommended title \"{Title}\"", item.Title);
            return null;
        }
    }
}
