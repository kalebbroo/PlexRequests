using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared.Releases;

/// <summary>What part of a release a specification looks at.</summary>
public enum FormatField
{
    ReleaseTitle = 0,
    Language = 1,
    AudioCodec = 2,
    AudioChannels = 3,
    VideoCodec = 4,
    Source = 5,
    Resolution = 6,
    Edition = 7,
    ReleaseGroup = 8,
    /// <summary>A provenance flag such as repack, internal, 10bit.</summary>
    Flag = 9,
    SizeGb = 10,
    AgeDays = 11
}

public enum FormatOp
{
    Regex = 0,
    Equals = 1,
    Contains = 2,
    GreaterThan = 3,
    LessThan = 4
}

/// <summary>
/// Matches releases against user-defined scoring rules.
///
/// The specification model is deliberately closed — a fixed field enum and a fixed operator enum — rather
/// than a general expression language. Everything people actually want to score on (a group, a language, an
/// audio codec, a size bound) fits, and a closed model can be validated at save time and reasoned about,
/// which an arbitrary expression evaluator cannot.
///
/// Pure, so the admin UI can run the identical matcher client-side for its live "test this release name"
/// box without a round-trip or a second implementation.
/// </summary>
public static class CustomFormatMatcher
{
    /// <summary>
    /// Cap on a user-supplied pattern. A regex from the admin panel runs inside the downloader's ranking
    /// loop against every candidate, so an accidental catastrophic-backtracking pattern would stall
    /// searches. Length is the cheap first guard; the timeout below is the real one.
    /// </summary>
    public const int MaxPatternLength = 512;

    private static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(250);

    /// <summary>Compiled patterns, keyed by the pattern text. Bounded by how many specs an admin writes.</summary>
    private static readonly Dictionary<string, Regex?> RegexCache = new();
    private static readonly object CacheLock = new();

    /// <summary>
    /// Total score for a release, plus which formats matched. Scores come from the profile, so the same
    /// format can be worth +100 in one profile and -1000 in another.
    /// </summary>
    public static (int Score, List<string> Matched) Score(
        ParsedRelease parsed, ReleaseCandidate candidate,
        IReadOnlyList<CustomFormatDto> formats, IReadOnlyDictionary<int, int> scoresByFormatId)
    {
        int total = 0;
        var matched = new List<string>();

        foreach (var format in formats)
        {
            if (!format.Enabled) continue;
            if (!Matches(format, parsed, candidate)) continue;
            matched.Add(format.Name);
            if (scoresByFormatId.TryGetValue(format.Id, out var points)) total += points;
        }
        return (total, matched);
    }

    /// <summary>
    /// Radarr's semantics: every REQUIRED specification must match, and at least one non-required one must
    /// too. A format made entirely of required specs matches when they all do.
    /// </summary>
    public static bool Matches(CustomFormatDto format, ParsedRelease parsed, ReleaseCandidate candidate)
    {
        if (format.Specifications.Count == 0) return false;

        bool anyOptionalMatched = false;
        bool hasOptional = false;

        foreach (var spec in format.Specifications)
        {
            var hit = MatchesSpec(spec, parsed, candidate);
            if (spec.Negate) hit = !hit;

            if (spec.Required)
            {
                if (!hit) return false;
            }
            else
            {
                hasOptional = true;
                if (hit) anyOptionalMatched = true;
            }
        }
        return !hasOptional || anyOptionalMatched;
    }

    public static bool MatchesSpec(FormatSpecificationDto spec, ParsedRelease parsed, ReleaseCandidate candidate)
    {
        return spec.Field switch
        {
            FormatField.ReleaseTitle => Compare(candidate.ReleaseName, spec),
            FormatField.Language => parsed.Languages.Any(l => Compare(l, spec)),
            FormatField.AudioCodec => Compare(parsed.AudioCodec, spec),
            FormatField.AudioChannels => Compare(parsed.AudioChannels, spec),
            FormatField.VideoCodec => Compare(parsed.Codec, spec),
            FormatField.Source => Compare(parsed.Source.ToString(), spec),
            FormatField.Resolution => CompareNumber(parsed.Resolution, spec),
            FormatField.Edition => Compare(parsed.Edition, spec),
            FormatField.ReleaseGroup => Compare(parsed.Group, spec),
            FormatField.Flag => parsed.Flags.Any(f => Compare(f, spec)),
            FormatField.SizeGb => candidate.SizeKnown && CompareNumber(candidate.SizeGb, spec),
            FormatField.AgeDays => candidate.AgeDays is double age && CompareNumber(age, spec),
            _ => false
        };
    }

    private static bool Compare(string? value, FormatSpecificationDto spec)
    {
        if (string.IsNullOrEmpty(value)) return false;
        return spec.Op switch
        {
            FormatOp.Equals => string.Equals(value, spec.Value, StringComparison.OrdinalIgnoreCase),
            FormatOp.Contains => value.Contains(spec.Value ?? string.Empty, StringComparison.OrdinalIgnoreCase),
            FormatOp.Regex => SafeRegexMatch(value, spec.Value),
            _ => false
        };
    }

    private static bool CompareNumber(double value, FormatSpecificationDto spec)
    {
        if (!double.TryParse(spec.Value, out var target)) return false;
        return spec.Op switch
        {
            FormatOp.GreaterThan => value > target,
            FormatOp.LessThan => value < target,
            FormatOp.Equals => Math.Abs(value - target) < 0.0001,
            _ => false
        };
    }

    /// <summary>
    /// Run a user pattern with both guards on: non-backtracking where the pattern allows it, and a hard
    /// timeout where it doesn't. A pattern that can't be compiled at all is treated as never matching
    /// rather than throwing into the ranking loop.
    /// </summary>
    private static bool SafeRegexMatch(string input, string? pattern)
    {
        var rx = GetRegex(pattern);
        if (rx is null) return false;
        try { return rx.IsMatch(input); }
        catch (RegexMatchTimeoutException) { return false; }
    }

    private static Regex? GetRegex(string? pattern)
    {
        if (string.IsNullOrWhiteSpace(pattern) || pattern.Length > MaxPatternLength) return null;

        lock (CacheLock)
        {
            if (RegexCache.TryGetValue(pattern, out var cached)) return cached;

            Regex? compiled = null;
            try
            {
                // NonBacktracking gives linear-time matching, which removes catastrophic backtracking
                // outright. It rejects some constructs (backreferences, lookaround), so fall back to a
                // normal regex with a timeout for those.
                compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);
            }
            catch (NotSupportedException)
            {
                try { compiled = new Regex(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, MatchTimeout); }
                catch (ArgumentException) { compiled = null; }
            }
            catch (ArgumentException) { compiled = null; }

            RegexCache[pattern] = compiled;
            return compiled;
        }
    }

    /// <summary>Whether a pattern is usable. Called at save time so a bad one is rejected in the UI rather
    /// than silently never matching.</summary>
    public static bool IsValidPattern(string? pattern, out string? error)
    {
        error = null;
        if (string.IsNullOrWhiteSpace(pattern)) { error = "A pattern is required."; return false; }
        if (pattern.Length > MaxPatternLength) { error = $"Patterns are limited to {MaxPatternLength} characters."; return false; }
        if (GetRegex(pattern) is null) { error = "That isn't a valid regular expression."; return false; }
        return true;
    }
}
