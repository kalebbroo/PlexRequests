using PlexRequests.Downloader.Download;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequests.Downloader.Worker;

internal sealed record AnimeEpisodeOrderResolution(
    bool Resolved,
    SeriesEpisodeOrderProfileDto? Profile,
    ManifestPreflightDecision? Decision,
    string Detail);

/// <summary>Compares an untrusted torrent manifest with server-authored TMDb episode-group snapshots.
/// Selection is intentionally conservative: one complete mapping must pass preflight, and every other
/// passing group must define the exact same full source-to-canonical contract. A match limited to the
/// current subset is not enough because persisting it also governs future episodes of the series.</summary>
internal static class AnimeEpisodeOrderResolver
{
    public static AnimeEpisodeOrderResolution Resolve(
        AcquisitionManifest manifest,
        FulfillmentJobDto job,
        DownloadPlanItem item,
        IReadOnlyCollection<SeriesEpisodeOrderProfileDto> candidates,
        IReleaseParser parser,
        IReadOnlyCollection<string> videoExtensions,
        double maxSelectedGb)
    {
        var evaluated = new List<(SeriesEpisodeOrderProfileDto Profile, ManifestPreflightDecision Decision)>();
        var failures = new List<string>();
        foreach (var profile in candidates
                     .Where(profile => profile.TmdbId > 0
                                       && profile.TmdbId == job.TmdbId
                                       && EpisodeOrderMapping.IsActive(profile)
                                       && !string.IsNullOrWhiteSpace(profile.SourceEpisodeGroupId))
                     .DistinctBy(profile => profile.SourceEpisodeGroupId, StringComparer.Ordinal))
        {
            if (!EpisodeOrderMapping.TryParse(profile, out _, out var mappingError))
            {
                failures.Add($"{Label(profile)}: invalid map ({mappingError})");
                continue;
            }

            var decision = AnimeManifestPreflight.EvaluateWithEpisodeOrder(manifest, job, item, parser,
                videoExtensions, maxSelectedGb, profile);
            if (decision.Accepted) evaluated.Add((profile, decision));
            else failures.Add($"{Label(profile)}: {decision.Detail}");
        }

        if (evaluated.Count == 0)
        {
            var reason = failures.Count == 0
                ? "TMDb supplied no usable episode groups for this series."
                : $"No TMDb episode group exactly matches the torrent manifest. {string.Join(" | ", failures.Take(3))}";
            return new AnimeEpisodeOrderResolution(false, null, null, reason);
        }

        var contracts = evaluated.GroupBy(match => Contract(match.Profile), StringComparer.Ordinal).ToList();
        if (contracts.Count != 1)
        {
            var names = string.Join(", ", evaluated.Select(match => Label(match.Profile)).Distinct().Take(8));
            return new AnimeEpisodeOrderResolution(false, null, null,
                $"Torrent manifest fits {contracts.Count} conflicting TMDb episode orders ({names}); admin review is required before any payload can start.");
        }

        var selected = contracts[0]
            .OrderBy(match => match.Profile.SourceEpisodeGroupName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(match => match.Profile.SourceEpisodeGroupId, StringComparer.Ordinal)
            .First();
        return new AnimeEpisodeOrderResolution(true, selected.Profile, selected.Decision,
            $"Manifest uniquely matches TMDb episode group \"{Label(selected.Profile)}\". {selected.Decision.Detail}");
    }

    private static string Contract(SeriesEpisodeOrderProfileDto profile)
    {
        var groups = string.Join('\n', (profile.SourceGroups ?? [])
            .OrderBy(group => group.SourceSeason)
            .Select(group => $"G{group.SourceSeason}:{group.Name.Trim()}"));
        return $"{profile.SourceOrder}\n{groups}\n{EpisodeOrderMapping.Normalize(profile)}";
    }

    private static string Label(SeriesEpisodeOrderProfileDto profile) =>
        string.IsNullOrWhiteSpace(profile.SourceEpisodeGroupName)
            ? profile.SourceEpisodeGroupId ?? "Unnamed group"
            : profile.SourceEpisodeGroupName.Trim();
}
