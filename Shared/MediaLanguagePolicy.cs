using System.Text.RegularExpressions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>Canonical language parsing and pure policy evaluation shared by web, worker, and tests.</summary>
public static class MediaLanguagePolicy
{
    // Audio-description ("descriptive") tracks narrate the picture for blind and low-vision viewers. They are
    // tagged with the spoken language, so a language check alone accepts them. They are never the audio a viewer
    // asked for, so they are not counted as a language and never chosen as a default.
    private static readonly Regex DescriptiveTrackTitle = new(
        @"\b(descriptive|described|description|visual(ly)?\s+impaired|dvs)\b|(?<![A-Za-z])(?-i:AD)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    private static readonly Regex DescriptiveReleaseName = new(
        @"(?<![A-Za-z])(descriptive|described|audio[ ._-]?description|visual[ ._-]?impaired|dvs)(?![A-Za-z])",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

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
        Preference = profile?.LanguagePreference ?? ReleaseLanguagePreference.Any,
        PreferredAudioLanguage = Normalize(profile?.PreferredAudioLanguage),
        PreferredSubtitleLanguage = Normalize(profile?.PreferredSubtitleLanguage),
        PreferForcedSubtitles = profile?.PreferForcedSubtitles == true,
        RequiredAudioLanguages = ParseCsv(profile?.RequiredAudioLanguagesCsv),
        AllowedAudioLanguages = ParseCsv(profile?.AllowedLanguagesCsv),
        RequiredSubtitleLanguages = ParseCsv(profile?.RequiredSubtitleLanguagesCsv),
        RequireForcedSubtitle = profile?.RequireForcedSubtitle == true,
        AllowUnknownTrackLanguage = profile?.AllowUnknownTrackLanguage ?? true,
        SetPreferredTracksAsDefault = profile?.SetPreferredTracksAsDefault ?? true
    };

    /// <summary>The safe catch-all used when a legacy video job has no resolvable profile.</summary>
    public static MediaLanguagePolicyDto SmartDefault() => new()
    {
        Preference = ReleaseLanguagePreference.Smart,
        PreferredAudioLanguage = "en",
        PreferredSubtitleLanguage = "en",
        PreferForcedSubtitles = true,
        AllowUnknownTrackLanguage = false,
        SetPreferredTracksAsDefault = true
    };

    public static bool IsActive(MediaLanguagePolicyDto? policy) => policy is not null &&
        (policy.Preference != ReleaseLanguagePreference.Any
         || policy.RequiredAudioLanguages.Count > 0 || policy.AllowedAudioLanguages.Count > 0
         || policy.RequiredSubtitleLanguages.Count > 0 || policy.RequireForcedSubtitle
         || policy.SetPreferredTracksAsDefault);

    /// <summary>True for an audio-description track, judged from its title.</summary>
    public static bool IsDescriptiveAudio(MediaTrackDto track) => IsDescriptiveTitle(track.Title);

    public static bool IsDescriptiveTitle(string? title) =>
        !string.IsNullOrWhiteSpace(title) && DescriptiveTrackTitle.IsMatch(title);

    /// <summary>True when a release name advertises an audio-description track. Only the release name is checked
    /// here; the inspected streams are what prove a track is present.</summary>
    public static bool ReleaseAdvertisesDescriptiveAudio(string? releaseName) =>
        !string.IsNullOrWhiteSpace(releaseName) && DescriptiveReleaseName.IsMatch(releaseName);

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
        MediaTrackSummaryDto tracks, bool isAnime = false)
    {
        if (!IsActive(policy)) return (true, null);
        var p = policy!;
        // Audio-description tracks are not spoken audio: they never satisfy a language requirement or the allowlist.
        var audio = tracks.Audio.Where(t => !IsDescriptiveAudio(t)).Select(t => Normalize(t.Language)).ToList();
        var subtitles = tracks.Subtitles.Select(t => Normalize(t.Language)).ToList();
        var knownAudio = audio.Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var knownSubtitles = subtitles.Where(x => x is not null).Select(x => x!).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var preferredAudio = Normalize(p.PreferredAudioLanguage) ?? "en";
        var preferredSubtitle = Normalize(p.PreferredSubtitleLanguage) ?? "en";
        if (p.Preference == ReleaseLanguagePreference.EnglishOnly && !knownAudio.Contains(preferredAudio))
            return (false, $"missing required {preferredAudio} audio; found: {Found(knownAudio)}{IgnoredDescriptive(tracks)}");
        if (p.Preference == ReleaseLanguagePreference.OriginalWithEnglishSubtitles
            && !knownSubtitles.Contains(preferredSubtitle))
            return (false, $"missing required {preferredSubtitle} subtitles; found: {Found(knownSubtitles)}");

        // Smart is deliberately permissive for ordinary foreign-language films: preferred audio wins, but
        // an original-language copy remains usable. Anime has a narrower safe fallback contract. It must
        // contain the preferred dub, or Japanese audio plus preferred-language subtitles. This keeps a
        // German/Japanese (or Italian/Japanese) multi-audio pack from importing merely because "Smart" used
        // to affect ranking only. Untagged streams remain compatible with the explicit admin setting.
        if (p.Preference == ReleaseLanguagePreference.Smart && isAnime && knownAudio.Count > 0
            && !knownAudio.Contains(preferredAudio))
        {
            var hasPreferredOrUnknownSubtitles = knownSubtitles.Contains(preferredSubtitle)
                || (p.AllowUnknownTrackLanguage && subtitles.Any(language => language is null));
            if (!knownAudio.Contains("ja") || !hasPreferredOrUnknownSubtitles)
                return (false,
                    $"Smart anime requires {preferredAudio} audio, or Japanese audio with {preferredSubtitle} subtitles; " +
                    $"found audio: {Found(knownAudio)}; subtitles: {Found(knownSubtitles)}");
        }
        if (p.Preference == ReleaseLanguagePreference.Smart && isAnime && knownAudio.Count == 0
            && !p.AllowUnknownTrackLanguage)
            return (false, "Smart anime could not verify an audio language because every audio track is untagged");

        var missingAudio = p.RequiredAudioLanguages.Select(Normalize).Where(x => x is not null)
            .Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(required => !knownAudio.Contains(required)).ToList();
        if (missingAudio.Count > 0)
            return (false, $"missing required audio language(s): {string.Join(", ", missingAudio)}; found: {Found(knownAudio)}{IgnoredDescriptive(tracks)}");

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

        if (p.RequireForcedSubtitle)
        {
            var forcedSubtitles = tracks.Subtitles.Where(t => t.IsForced).ToList();
            if (forcedSubtitles.Count == 0)
                return (false, "no forced subtitle track was found");

            // When an admin names a preferred subtitle language, "forced required" means a forced track
            // in that language. A release with English full subtitles plus (for example) Russian forced
            // subtitles must not pass an English replacement policy.
            var forcedLanguage = Normalize(p.PreferredSubtitleLanguage);
            if (forcedLanguage is not null && !forcedSubtitles.Any(t =>
                    string.Equals(Normalize(t.Language), forcedLanguage, StringComparison.OrdinalIgnoreCase)))
                return (false, $"missing required {forcedLanguage} forced subtitles");
        }

        return (true, null);
    }

    private static string IgnoredDescriptive(MediaTrackSummaryDto tracks)
    {
        var languages = tracks.Audio.Where(IsDescriptiveAudio)
            .Select(t => Normalize(t.Language) ?? "untagged").Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return languages.Count == 0 ? string.Empty
            : $"; audio-description track(s) ignored: {string.Join(", ", languages)}";
    }

    private static string Found(HashSet<string> languages) => languages.Count == 0
        ? "none/untagged"
        : string.Join(", ", languages.OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
}
