using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>A unique canonical-season name found in an anime release title. Uploaders often call an arc
/// "S04" while TMDb/Plex call that same named arc S03; the name is stronger evidence than the number.</summary>
public sealed record AnimeSeasonIdentityMatch(int SourceSeason, int CanonicalSeason, string CanonicalName);

public static partial class AnimeSeasonIdentity
{
    private static readonly HashSet<string> GenericTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "of", "the", "season", "series", "part", "cour", "volume", "vol", "chapter", "tv"
    };

    /// <summary>Resolve only a unique, distinctive canonical season-name match. A release must also carry
    /// the requested series identity (token or compact substring), so a generic name such as "Second
    /// Season" cannot capture an unrelated search result. Generic provider names like "Season 2" produce
    /// no evidence and deliberately fall back to normal numeric handling.</summary>
    public static AnimeSeasonIdentityMatch? Match(string releaseName, string seriesTitle,
        IReadOnlyCollection<SeasonTarget> targets, int? sourceSeason)
        => Match(releaseName, seriesTitle, targets.Select(target => new CanonicalSeasonIdentityDto
        {
            Season = target.Season,
            Name = target.Name,
            EpisodeCount = target.EpisodeCount
        }).ToList(), sourceSeason);

    public static AnimeSeasonIdentityMatch? Match(string releaseName, string seriesTitle,
        IReadOnlyCollection<CanonicalSeasonIdentityDto> targets, int? sourceSeason)
    {
        if (sourceSeason is null || targets.Count == 0 || string.IsNullOrWhiteSpace(releaseName)
            || string.IsNullOrWhiteSpace(seriesTitle)) return null;

        var releaseTokens = Tokens(releaseName);
        var titleTokens = Tokens(seriesTitle);
        var releaseCompact = string.Concat(releaseTokens);
        var titleCompact = string.Concat(titleTokens);
        var carriesSeriesIdentity = titleTokens.Count > 0 && titleTokens.All(releaseTokens.Contains)
            || titleCompact.Length >= 5 && releaseCompact.Contains(titleCompact, StringComparison.Ordinal);
        if (!carriesSeriesIdentity) return null;

        var matches = new List<(CanonicalSeasonIdentityDto Target, List<string> Evidence)>();
        foreach (var target in targets.Where(target => !string.IsNullOrWhiteSpace(target.Name)))
        {
            var evidence = Tokens(target.Name!)
                .Where(token => !GenericTokens.Contains(token) && !titleTokens.Contains(token)
                                && !token.All(char.IsDigit))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            if (evidence.Count == 0) continue;
            var compact = string.Concat(evidence);
            if (evidence.All(releaseTokens.Contains)
                || compact.Length >= 5 && releaseCompact.Contains(compact, StringComparison.Ordinal))
                matches.Add((target, evidence));
        }

        var canonical = matches.Select(match => match.Target.Season).Distinct().ToList();
        if (canonical.Count != 1) return null;
        var selected = matches.First(match => match.Target.Season == canonical[0]).Target;
        return new AnimeSeasonIdentityMatch(sourceSeason.Value, selected.Season, selected.Name!.Trim());
    }

    /// <summary>Whether a filename/path carries the same unique named-season identity already proven by
    /// the outer release. This is intentionally stricter than recognizing a bare absolute episode number:
    /// the series and distinctive canonical season name must both survive into the internal path.</summary>
    public static bool MatchesCanonicalSeason(string releaseName, string seriesTitle,
        IReadOnlyCollection<SeasonTarget> targets, int sourceSeason, int canonicalSeason) =>
        Match(releaseName, seriesTitle, targets, sourceSeason)?.CanonicalSeason == canonicalSeason;

    public static bool MatchesCanonicalSeason(string releaseName, string seriesTitle,
        IReadOnlyCollection<CanonicalSeasonIdentityDto> targets, int sourceSeason, int canonicalSeason) =>
        Match(releaseName, seriesTitle, targets, sourceSeason)?.CanonicalSeason == canonicalSeason;

    private static HashSet<string> Tokens(string value) => WordRegex().Matches(value.ToLowerInvariant())
        .Select(match => match.Value).ToHashSet(StringComparer.OrdinalIgnoreCase);

    [GeneratedRegex("[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordRegex();
}
