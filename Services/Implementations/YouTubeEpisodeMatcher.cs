using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PlexRequestsHosted.Services.Abstractions;

namespace PlexRequestsHosted.Services.Implementations;

public sealed record DirectVideoMatch(YouTubeCatalogVideo Video, bool Accepted, IReadOnlyList<string> Reasons);

/// <summary>The result for one episode. <see cref="Selected"/> is set only when exactly one upload passes every rule.</summary>
public sealed record DirectVideoMatchOutcome(YouTubeCatalogVideo? Selected, IReadOnlyList<DirectVideoMatch> Candidates);

/// <summary>Decides whether a channel upload is one specific TV episode. It is strict on purpose. The show name must
/// appear; the episode must be named by an explicit season/episode token or by its exact episode title; any token that
/// names a different episode rejects the video; and exactly one video must survive. An ambiguous result is "no match",
/// never a guess, because a wrong frozen identity would import as the wrong episode.</summary>
public static partial class YouTubeEpisodeMatcher
{
    /// <summary>Shorter titles ("Pilot", "Part 1") are too generic to identify an episode without a token.</summary>
    public const int MinimumEpisodeTitleLength = 6;
    public const int MinimumRuntimeSeconds = 3 * 60;
    public const int MaximumRuntimeSeconds = 45 * 60;

    private static readonly string[] RejectedPhrases =
    [
        "compilation", "marathon", "mix", "playlist", "reaction", "review", "clip", "clips", "song", "songs",
        "music video", "trailer", "teaser", "preview", "all episodes", "full series", "shorts"
    ];

    /// <param name="otherEpisodeTitles">Titles of the other known episodes of the season. An upload naming any of them
    /// is a shared two-episode upload, so it cannot stand for this episode even when its token says otherwise.</param>
    public static DirectVideoMatchOutcome Match(string showTitle, int season, int episode, string? episodeTitle,
        IReadOnlyCollection<string> allowedChannelIds, IEnumerable<YouTubeCatalogVideo> catalog,
        IReadOnlyCollection<string>? otherEpisodeTitles = null)
    {
        var allowed = allowedChannelIds.ToHashSet(StringComparer.Ordinal);
        var others = (otherEpisodeTitles ?? []).Select(Normalize).Where(t => t.Length >= MinimumEpisodeTitleLength).ToList();
        var evaluations = catalog
            .DistinctBy(video => video.VideoId, StringComparer.Ordinal)
            .Select(video => Evaluate(video, showTitle, season, episode, episodeTitle, allowed, others))
            .ToList();

        var accepted = evaluations.Where(x => x.Accepted).ToList();
        if (accepted.Count == 1) return new DirectVideoMatchOutcome(accepted[0].Video, evaluations);

        if (accepted.Count > 1)
        {
            evaluations = evaluations.Select(x => x.Accepted
                ? x with { Reasons = [$"ambiguous: {accepted.Count} uploads match S{season:D2}E{episode:D2}"] }
                : x).ToList();
        }
        return new DirectVideoMatchOutcome(null, evaluations);
    }

    private static DirectVideoMatch Evaluate(YouTubeCatalogVideo video, string showTitle, int season, int episode,
        string? episodeTitle, IReadOnlySet<string> allowed, IReadOnlyList<string> otherTitles)
    {
        var reasons = new List<string>();
        var raw = (video.Title ?? string.Empty).ToLowerInvariant();
        var title = Normalize(video.Title);
        var padded = $" {title} ";

        if (!allowed.Contains(video.ChannelId)) reasons.Add("channel is not on the allowlist");
        if (video.IsLive) reasons.Add("live stream");
        if (video.DurationSeconds < MinimumRuntimeSeconds || video.DurationSeconds > MaximumRuntimeSeconds)
            reasons.Add($"runtime {video.DurationSeconds / 60}m{video.DurationSeconds % 60:D2}s is outside 3-45 minutes");
        if (!ShowMatches(padded, showTitle)) reasons.Add("show name not found in title");

        var phrase = RejectedPhrases.FirstOrDefault(p => padded.Contains($" {p} ", StringComparison.Ordinal));
        if (phrase is not null) reasons.Add($"title contains \"{phrase}\"");
        if (MultiEpisodeRange().IsMatch(raw) || MultiPart().IsMatch(raw))
            reasons.Add("multi-episode or multi-part upload");

        var tokens = EpisodeTokens(title);
        var target = (Season: season, Episode: episode);
        if (tokens.Any(token => token != target)) reasons.Add("title names a different season or episode");
        var namedTarget = tokens.Contains(target);
        var titled = IsEpisodeTitleMatch(padded, episodeTitle);
        if (!namedTarget && !titled)
            reasons.Add($"no S{season:D2}E{episode:D2} token and no exact episode title");
        if (otherTitles.Any(other => padded.Contains($" {other} ", StringComparison.Ordinal)))
            reasons.Add("title also names another episode of the season");

        return new DirectVideoMatch(video, reasons.Count == 0, reasons);
    }

    private static bool ShowMatches(string padded, string showTitle)
    {
        var full = Normalize(showTitle);
        var names = new List<string> { full };
        foreach (var prefix in new[] { "marvels ", "the " })
            if (full.StartsWith(prefix, StringComparison.Ordinal)) names.Add(full[prefix.Length..]);
        return names.Any(name => name.Length > 0 && padded.Contains($" {name} ", StringComparison.Ordinal));
    }

    private static bool IsEpisodeTitleMatch(string padded, string? episodeTitle)
    {
        var normalized = Normalize(episodeTitle);
        return normalized.Length >= MinimumEpisodeTitleLength && padded.Contains($" {normalized} ", StringComparison.Ordinal);
    }

    private static HashSet<(int Season, int Episode)> EpisodeTokens(string normalized)
    {
        var tokens = new HashSet<(int, int)>();
        foreach (Match match in SeasonEpisodeToken().Matches(normalized))
            tokens.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        foreach (Match match in SeasonWordEpisodeToken().Matches(normalized))
            tokens.Add((int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
                int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture)));
        return tokens;
    }

    /// <summary>Lowercases, strips accents, drops apostrophes so "Marvel's" becomes "marvels", and turns every other
    /// punctuation mark into a word break. Two titles compare equal only when their words line up.</summary>
    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var builder = new StringBuilder(value.Length);
        foreach (var c in value.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
            if (c is '\'' or '’') continue;
            builder.Append(char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : ' ');
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    [GeneratedRegex(@"(?<![a-z0-9])s(\d{1,2})\s?e(\d{1,3})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonEpisodeToken();

    [GeneratedRegex(@"(?<![a-z0-9])season\s?(\d{1,2})\s?episode\s?(\d{1,3})(?![0-9])", RegexOptions.CultureInvariant)]
    private static partial Regex SeasonWordEpisodeToken();

    [GeneratedRegex(@"\bepisodes?\s*\d{1,3}\s*(?:-|–|&|and|to)\s*\d{1,3}\b|\bs\d{1,2}\s*e\d{1,3}\s*-\s*e?\d{1,3}\b",
        RegexOptions.CultureInvariant)]
    private static partial Regex MultiEpisodeRange();

    [GeneratedRegex(@"\bpart\s*\d+\b|\bpt\.?\s*\d+\b", RegexOptions.CultureInvariant)]
    private static partial Regex MultiPart();
}
