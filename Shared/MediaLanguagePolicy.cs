using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Shared;

/// <summary>Canonical language parsing and pure policy evaluation shared by web, worker, and tests.</summary>
public static class MediaLanguagePolicy
{
    private static readonly Dictionary<string, string> Aliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["en"] = "en", ["eng"] = "en", ["english"] = "en",
        ["ja"] = "ja", ["jpn"] = "ja", ["japanese"] = "ja",
        ["es"] = "es", ["spa"] = "es", ["spanish"] = "es",
        ["fr"] = "fr", ["fre"] = "fr", ["fra"] = "fr", ["french"] = "fr",
        ["de"] = "de", ["deu"] = "de", ["ger"] = "de", ["german"] = "de",
        ["it"] = "it", ["ita"] = "it", ["italian"] = "it",
        ["pt"] = "pt", ["por"] = "pt", ["portuguese"] = "pt",
        ["nl"] = "nl", ["nld"] = "nl", ["dut"] = "nl", ["dutch"] = "nl",
        ["sv"] = "sv", ["swe"] = "sv", ["swedish"] = "sv",
        ["da"] = "da", ["dan"] = "da", ["danish"] = "da",
        ["no"] = "no", ["nor"] = "no", ["norwegian"] = "no",
        ["fi"] = "fi", ["fin"] = "fi", ["finnish"] = "fi",
        ["pl"] = "pl", ["pol"] = "pl", ["polish"] = "pl",
        ["ru"] = "ru", ["rus"] = "ru", ["russian"] = "ru",
        ["ar"] = "ar", ["ara"] = "ar", ["arabic"] = "ar",
        ["hi"] = "hi", ["hin"] = "hi", ["hindi"] = "hi",
        ["ta"] = "ta", ["tam"] = "ta", ["tamil"] = "ta",
        ["te"] = "te", ["tel"] = "te", ["telugu"] = "te",
        ["ko"] = "ko", ["kor"] = "ko", ["korean"] = "ko",
        ["zh"] = "zh", ["zho"] = "zh", ["chi"] = "zh", ["chinese"] = "zh"
    };

    public static MediaLanguagePolicyDto FromProfile(QualityProfileDto? profile) => new()
    {
        RequiredAudioLanguages = ParseCsv(profile?.RequiredAudioLanguagesCsv),
        AllowedAudioLanguages = ParseCsv(profile?.AllowedLanguagesCsv),
        RequiredSubtitleLanguages = ParseCsv(profile?.RequiredSubtitleLanguagesCsv),
        RequireForcedSubtitle = profile?.RequireForcedSubtitle == true,
        AllowUnknownTrackLanguage = profile?.AllowUnknownTrackLanguage ?? true
    };

    public static bool IsActive(MediaLanguagePolicyDto? policy) => policy is not null &&
        (policy.RequiredAudioLanguages.Count > 0 || policy.AllowedAudioLanguages.Count > 0
         || policy.RequiredSubtitleLanguages.Count > 0 || policy.RequireForcedSubtitle);

    public static string? Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var clean = value.Trim().Replace('_', '-');
        if (clean.Equals("und", StringComparison.OrdinalIgnoreCase)
            || clean.Equals("unknown", StringComparison.OrdinalIgnoreCase)) return null;
        var primary = clean.Split('-', 2)[0];
        return Aliases.TryGetValue(primary, out var normalized) ? normalized : primary.ToLowerInvariant();
    }

    /// <summary>Normalizes a concrete language token, excluding ambiguous release tags such as dual/multi.</summary>
    public static string? NormalizeExplicitLanguage(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var primary = value.Trim().Replace('_', '-').Split('-', 2)[0];
        return Aliases.TryGetValue(primary, out var normalized) ? normalized : null;
    }

    public static List<string> ParseCsv(string? csv) => string.IsNullOrWhiteSpace(csv)
        ? new List<string>()
        : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(Normalize).Where(x => x is not null).Select(x => x!)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    public static (bool Accepted, string? Reason) Evaluate(MediaLanguagePolicyDto? policy,
        MediaTrackSummaryDto tracks)
    {
        if (!IsActive(policy)) return (true, null);
        var p = policy!;
        var audio = tracks.Audio.Select(t => Normalize(t.Language)).ToList();
        var subtitles = tracks.Subtitles.Select(t => Normalize(t.Language)).ToList();
        var knownAudio = audio.Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownSubtitles = subtitles.Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var missingAudio = p.RequiredAudioLanguages.Select(Normalize).Where(x => x is not null)
            .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(required => !knownAudio.Contains(required)).ToList();
        if (missingAudio.Count > 0)
            return (false, $"missing required audio language(s): {string.Join(", ", missingAudio)}; found: {Found(knownAudio)}");

        var allowedAudio = p.AllowedAudioLanguages.Select(Normalize).Where(x => x is not null)
            .Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var disallowedAudio = allowedAudio.Count == 0 ? new List<string>()
            : knownAudio.Where(language => !allowedAudio.Contains(language)).ToList();
        if (disallowedAudio.Count > 0)
            return (false, $"audio language(s) outside the allowed set: {string.Join(", ", disallowedAudio)}");

        var missingSubtitles = p.RequiredSubtitleLanguages.Select(Normalize).Where(x => x is not null)
            .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(required => !knownSubtitles.Contains(required)).ToList();
        if (missingSubtitles.Count > 0)
            return (false, $"missing required subtitle language(s): {string.Join(", ", missingSubtitles)}; found: {Found(knownSubtitles)}");

        if (!p.AllowUnknownTrackLanguage)
        {
            if ((p.RequiredAudioLanguages.Count > 0 || p.AllowedAudioLanguages.Count > 0)
                && audio.Any(x => x is null))
                return (false, "one or more audio tracks have no language tag");
            if ((p.RequiredSubtitleLanguages.Count > 0 || p.RequireForcedSubtitle)
                && subtitles.Any(x => x is null))
                return (false, "one or more subtitle tracks have no language tag");
        }

        if (p.RequireForcedSubtitle && !tracks.Subtitles.Any(t => t.IsForced))
            return (false, "no forced subtitle track was found");

        return (true, null);
    }

    private static string Found(HashSet<string> languages) => languages.Count == 0
        ? "none/untagged"
        : string.Join(", ", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}
