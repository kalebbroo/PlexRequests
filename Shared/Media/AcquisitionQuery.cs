using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared.Media;

/// <summary>One provider-neutral query builder shared by every free-text indexer adapter.</summary>
public static class AcquisitionQuery
{
    public static string Build(FulfillmentJobDto job, bool includeMovieYear = true)
    {
        if (job.MediaType == MediaType.Music)
            return job.Music?.BuildSearchText(job.Title) ?? job.Title;
        return includeMovieYear && job.MediaType == MediaType.Movie && job.Year is int year
            ? $"{job.Title} {year}"
            : job.Title;
    }

    /// <summary>
    /// Builds the bounded query set used by page-limited indexers. Alternate-order jobs are translated
    /// from Plex's canonical targets back to the season/episode notation actually present in releases.
    /// </summary>
    public static IReadOnlyList<string> BuildScoped(FulfillmentJobDto job, int maxScopes = 4)
    {
        var queries = new List<string> { Build(job) };
        if (maxScopes <= 0 || job.MediaType is not (MediaType.TvShow or MediaType.Anime)) return queries;

        if (!EpisodeOrderMapping.IsActive(job.EpisodeOrderProfile))
        {
            var seasons = job.RequestedSeasons
                .Concat(job.SeasonTargets.Select(t => t.Season))
                .Concat(job.RequestedEpisodes.Select(e => e.Season))
                .Distinct().OrderBy(s => s).Take(maxScopes);
            queries.AddRange(seasons.Select(s => $"{job.Title} season {s}"));
            return Distinct(queries);
        }

        var profile = job.EpisodeOrderProfile!;
        var scoped = new List<string>();

        // Explicit episode requests should be searched as that exact source episode first.
        foreach (var target in job.RequestedEpisodes)
            if (EpisodeOrderMapping.TryTranslateCanonicalToSource(profile, target.Season, target.Episode, out var source))
                scoped.Add(EpisodeTerm(job.Title, source));

        // Season requests prefer source-order season packs. An absolute-order source has no meaningful
        // season pack notation, so its individual mapped episodes are added below instead.
        var seasonScopes = job.RequestedSeasons.Concat(job.SeasonTargets.Select(t => t.Season)).Distinct().ToList();
        var seasonSources = EpisodeOrderMapping.SourcesForCanonicalSeasons(profile, seasonScopes);
        scoped.AddRange(seasonSources.Where(x => x.Season > 0).Select(x => x.Season).Distinct()
            .Select(season => $"{job.Title} season {season}"));

        // Missing episodes from a partially-filled season need exact source-order searches. For an
        // absolute-order series this is also the only useful scope narrower than the title-only query.
        foreach (var target in job.SeasonTargets.SelectMany(t => t.MissingEpisodes
                     .Select(episode => new EpisodeRef { Season = t.Season, Episode = episode })))
            if (EpisodeOrderMapping.TryTranslateCanonicalToSource(profile, target.Season, target.Episode, out var source))
                scoped.Add(EpisodeTerm(job.Title, source));
        scoped.AddRange(seasonSources.Where(x => x.Season == 0).Select(x => EpisodeTerm(job.Title, x)));

        queries.AddRange(scoped.Distinct(StringComparer.OrdinalIgnoreCase).Take(maxScopes));
        return Distinct(queries);
    }

    private static string EpisodeTerm(string title, EpisodeRef source) => source.Season == 0
        ? $"{title} {source.Episode}"
        : $"{title} S{source.Season:D2}E{source.Episode:D2}";

    private static IReadOnlyList<string> Distinct(IEnumerable<string> queries) =>
        queries.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
}
