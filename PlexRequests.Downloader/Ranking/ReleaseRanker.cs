using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Configuration;
using PlexRequests.Downloader.Indexers;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Ranking;

public interface IReleaseRanker
{
    /// <summary>Filter candidates to acceptable ones and return the highest-scoring, or null if none qualify.</summary>
    ReleaseCandidate? PickBest(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job);
}

/// <summary>
/// Enforces the requested quality floor + seeder/size thresholds, then scores survivors by
/// resolution, source, seeders, codec efficiency, proper/repack and preferred groups. Weights are
/// intentionally simple and tunable via <see cref="QualityOptions"/>.
/// </summary>
public class ReleaseRanker(IReleaseParser parser, IOptions<QualityOptions> quality, ILogger<ReleaseRanker> logger)
    : IReleaseRanker
{
    private readonly IReleaseParser _parser = parser;
    private readonly QualityOptions _q = quality.Value;
    private readonly ILogger<ReleaseRanker> _logger = logger;

    public ReleaseCandidate? PickBest(IReadOnlyList<ReleaseCandidate> candidates, FulfillmentJobDto job)
    {
        int floor = (int)job.Quality; // Quality enum values are the pixel heights; Any = 0

        var ranked = candidates
            .Select(c => new { c, p = _parser.Parse(c.ReleaseName), res = EffectiveResolution(c) })
            .Where(x =>
                (floor == 0 || x.res >= floor) &&
                x.c.Seeders >= _q.MinSeeders &&
                x.c.SizeGb <= _q.MaxSizeGb &&
                x.c.SizeGb >= 0.05) // reject 0-byte / fake entries
            .Select(x => new { x.c, score = Score(x.c, x.p, x.res, floor) })
            .OrderByDescending(x => x.score)
            .ToList();

        if (ranked.Count == 0)
        {
            _logger.LogInformation("No acceptable release for \"{Title}\" (of {Total} candidate(s); floor={Floor}, minSeeders={Min})",
                job.Title, candidates.Count, floor, _q.MinSeeders);
            return null;
        }

        var winner = ranked[0].c;
        _logger.LogInformation("Selected \"{Name}\" ({Seeders} seeders, {SizeGb:F1}GB, score {Score:F0}) for \"{Title}\"",
            winner.ReleaseName, winner.Seeders, winner.SizeGb, ranked[0].score, job.Title);
        return winner;
    }

    private int EffectiveResolution(ReleaseCandidate c)
    {
        var fromLabel = _parser.ResolutionFromLabel(c.QualityLabel);
        return fromLabel > 0 ? fromLabel : _parser.Parse(c.ReleaseName).Resolution;
    }

    private double Score(ReleaseCandidate c, ParsedRelease p, int res, int floor)
    {
        double s = 0;
        if (floor > 0) s += res >= floor ? 500 : -1000;
        s += Math.Min(res, 2160) / 10.0;                 // mild preference for higher resolution
        s += (int)p.Source * 60;                          // BluRay/Remux > WebDl > WebRip > HDTV
        s += Math.Log10(Math.Max(1, c.Seeders)) * 80;     // seeders, diminishing returns
        if (p.ProperOrRepack) s += 20;
        if (p.Codec == "x265") s += 15;                    // efficient size for same quality
        if (p.Hdr) s += 10;
        if (p.Group is not null && _q.PreferredGroups.Contains(p.Group, StringComparer.OrdinalIgnoreCase)) s += 100;
        return s;
    }
}
