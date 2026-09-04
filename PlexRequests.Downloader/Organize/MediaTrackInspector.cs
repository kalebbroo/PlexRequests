using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Organize;

public interface IMediaTrackInspector
{
    Task<MediaTrackSummaryDto> InspectAsync(string mediaPath, IReadOnlyList<string> companionSubtitles,
        CancellationToken ct);

    /// <summary>Changes only the staged library copy. Implementations must never edit the torrent payload.</summary>
    bool SetDefaults(string stagedPath, string sourceExtension, MediaTrackDefaultSelection selection) => false;
}

public sealed record MediaTrackDefaultSelection(
    int AudioCount, int? AudioOrdinal, int SubtitleCount, int? SubtitleOrdinal, bool EditSubtitles)
{
    /// <summary>Builds a deterministic playback plan from actual tracks. Ordinals are one-based within
    /// each track type, matching mkvpropedit's a1/s1 selectors.</summary>
    public static MediaTrackDefaultSelection? Create(MediaLanguagePolicyDto? policy,
        MediaTrackSummaryDto tracks, bool isAnime)
    {
        if (policy?.SetPreferredTracksAsDefault != true) return null;

        var preferredAudio = isAnime && policy.Preference == ReleaseLanguagePreference.Smart
            ? "ja"
            : MediaLanguagePolicy.Normalize(policy.PreferredAudioLanguage) ?? "en";
        var preferredSubtitle = MediaLanguagePolicy.Normalize(policy.PreferredSubtitleLanguage) ?? "en";

        var audio = Find(tracks.Audio, preferredAudio, preferFull: false)
                    ?? ExistingDefault(tracks.Audio);
        int? subtitle;
        var editSubtitles = false;
        if (isAnime && policy.Preference == ReleaseLanguagePreference.Smart)
        {
            // A normal/full translation is the useful default for Japanese anime. Forced/signs-only and
            // SDH tracks remain fallbacks when that is all the release contains.
            subtitle = Find(tracks.Subtitles, preferredSubtitle, preferFull: true)
                       ?? Find(tracks.Subtitles, preferredSubtitle, preferFull: false);
            editSubtitles = subtitle is not null;
        }
        else if (audio is int audioOrdinal
                 && MediaLanguagePolicy.Normalize(tracks.Audio[audioOrdinal - 1].Language) == preferredAudio)
        {
            subtitle = policy.PreferForcedSubtitles
                ? FindForced(tracks.Subtitles, preferredSubtitle)
                : null;
            // With preferred-language audio, no subtitle should remain default unless it is the requested
            // forced/signs track. This prevents a release's full foreign subtitle from staying enabled.
            editSubtitles = tracks.Subtitles.Any(x => !x.IsExternal);
        }
        else
        {
            // Foreign/original audio needs a full preferred-language translation when available.
            subtitle = Find(tracks.Subtitles, preferredSubtitle, preferFull: true)
                       ?? Find(tracks.Subtitles, preferredSubtitle, preferFull: false);
            editSubtitles = subtitle is not null;
        }

        var audioCount = tracks.Audio.Count(x => !x.IsExternal);
        var subtitleCount = tracks.Subtitles.Count(x => !x.IsExternal);
        return audio is null && !editSubtitles
            ? null
            : new(audioCount, audio, subtitleCount, subtitle, editSubtitles);
    }

    private static int? Find(IReadOnlyList<MediaTrackDto> tracks, string language, bool preferFull)
    {
        var candidates = tracks.Where(track => !track.IsExternal)
            .Select((track, index) => (track, ordinal: index + 1))
            .Where(x => MediaLanguagePolicy.Normalize(x.track.Language) == language);
        if (preferFull)
            candidates = candidates.OrderBy(x => x.track.IsForced)
                .ThenBy(x => IsAccessibilityOrCommentary(x.track.Title))
                .ThenByDescending(x => x.track.IsDefault);
        else
            candidates = candidates.OrderBy(x => IsAccessibilityOrCommentary(x.track.Title))
                .ThenByDescending(x => x.track.IsDefault);
        return candidates.Select(x => (int?)x.ordinal).FirstOrDefault();
    }

    private static int? FindForced(IReadOnlyList<MediaTrackDto> tracks, string language) =>
        tracks.Where(track => !track.IsExternal)
            .Select((track, index) => (track, ordinal: index + 1))
            .Where(x => x.track.IsForced
                        && MediaLanguagePolicy.Normalize(x.track.Language) == language)
            .OrderBy(x => IsAccessibilityOrCommentary(x.track.Title))
            .Select(x => (int?)x.ordinal).FirstOrDefault();

    private static int? ExistingDefault(IReadOnlyList<MediaTrackDto> tracks)
        => tracks.Where(track => !track.IsExternal)
            .Select((track, index) => (track, ordinal: index + 1))
            .Where(x => x.track.IsDefault)
            .Select(x => (int?)x.ordinal).FirstOrDefault()
           ?? tracks.Where(track => !track.IsExternal)
               .Select((track, index) => (track, ordinal: index + 1))
               .Select(x => (int?)x.ordinal).FirstOrDefault();

    private static bool IsAccessibilityOrCommentary(string? title) => title is not null &&
        (title.Contains("sdh", StringComparison.OrdinalIgnoreCase)
         || title.Contains("hearing", StringComparison.OrdinalIgnoreCase)
         || title.Contains("commentary", StringComparison.OrdinalIgnoreCase)
         || title.Contains("closed caption", StringComparison.OrdinalIgnoreCase)
         || title.Equals("cc", StringComparison.OrdinalIgnoreCase));
}

/// <summary>Reads the actual stream inventory with MediaInfo. Release-title tags are useful search hints,
/// but only this boundary is allowed to prove a strict audio/subtitle requirement.</summary>
public sealed class MediaInfoTrackInspector(ILogger<MediaInfoTrackInspector> logger) : IMediaTrackInspector
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromMinutes(2);

    public async Task<MediaTrackSummaryDto> InspectAsync(string mediaPath,
        IReadOnlyList<string> companionSubtitles, CancellationToken ct)
    {
        var start = new ProcessStartInfo
        {
            FileName = "mediainfo",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--Output=JSON");
        start.ArgumentList.Add(mediaPath);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("MediaInfo did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("MediaInfo is unavailable; strict media policy cannot be verified", ex);
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(ProbeTimeout);
        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException($"MediaInfo exceeded {ProbeTimeout.TotalSeconds:F0}s for '{Path.GetFileName(mediaPath)}'");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"MediaInfo could not inspect '{Path.GetFileName(mediaPath)}': {Clean(stderr)}");

        MediaTrackSummaryDto result;
        try { result = ParseOutput(stdout, companionSubtitles); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"MediaInfo returned invalid metadata for '{Path.GetFileName(mediaPath)}'", ex);
        }
        logger.LogDebug("Inspected {File}: {Audio} audio and {Subtitles} subtitle track(s)",
            Path.GetFileName(mediaPath), result.Audio.Count, result.Subtitles.Count);
        return result;
    }

    public bool SetDefaults(string stagedPath, string sourceExtension, MediaTrackDefaultSelection selection)
    {
        if (!sourceExtension.Equals(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            logger.LogWarning("Automatic playback defaults are not supported for {Extension}; preserving release flags for {File}",
                sourceExtension, Path.GetFileName(stagedPath));
            return false;
        }

        var start = new ProcessStartInfo
        {
            FileName = "mkvpropedit",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add(stagedPath);
        if (selection.AudioOrdinal is not null)
            AddEdits(start, "a", selection.AudioOrdinal, selection.AudioCount);
        if (selection.EditSubtitles)
            AddEdits(start, "s", selection.SubtitleOrdinal, selection.SubtitleCount);
        Run(start, stagedPath);
        return true;
    }

    private static void AddEdits(ProcessStartInfo start, string type, int? selected, int count)
    {
        for (var ordinal = 1; ordinal <= count; ordinal++)
        {
            start.ArgumentList.Add("--edit");
            start.ArgumentList.Add($"track:{type}{ordinal}");
            start.ArgumentList.Add("--set");
            start.ArgumentList.Add($"flag-default={(selected == ordinal ? 1 : 0)}");
        }
    }

    private static void Run(ProcessStartInfo start, string path)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("mkvpropedit did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("mkvpropedit is unavailable; playback defaults could not be applied", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)TimeSpan.FromMinutes(2).TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"mkvpropedit exceeded 120 seconds for '{Path.GetFileName(path)}'");
        }
        Task.WaitAll(stdout, stderr);
        // MKVToolNix uses 1 for a warning and 2 for a hard error.
        if (process.ExitCode > 1)
            throw new InvalidOperationException($"mkvpropedit could not update '{Path.GetFileName(path)}': {Clean(stderr.Result)}");
    }

    internal static MediaTrackSummaryDto ParseOutput(string json, IReadOnlyList<string> companionSubtitles)
    {
        var document = JsonSerializer.Deserialize<MediaInfoDocument>(json, Json) ?? new();
        var result = new MediaTrackSummaryDto();
        var audioOrdinal = 0;
        var subtitleOrdinal = 0;
        foreach (var stream in document.Media?.Tracks ?? [])
        {
            var type = stream.Type?.ToLowerInvariant() switch
            {
                "video" => "video",
                "audio" => "audio",
                "text" or "subtitle" => "subtitle",
                _ => null
            };
            if (type is null) continue;
            if (type == "video")
            {
                result.HasVideo = true;
                continue;
            }
            var track = new MediaTrackDto
            {
                Index = type == "audio" ? ++audioOrdinal : ++subtitleOrdinal,
                Type = type,
                Codec = stream.Format ?? stream.CodecId,
                Language = MediaLanguagePolicy.Normalize(stream.Language),
                Title = stream.Title,
                IsDefault = IsYes(stream.Default),
                IsForced = IsYes(stream.Forced)
            };
            if (type == "audio") result.Audio.Add(track); else result.Subtitles.Add(track);
        }

        foreach (var subtitle in companionSubtitles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tokens = Path.GetFileNameWithoutExtension(subtitle)
                .Split(['.', ' ', '_', '-'], StringSplitOptions.RemoveEmptyEntries);
            var language = tokens.Reverse().Where(IsLanguageFilenameToken)
                .Select(MediaLanguagePolicy.Normalize).FirstOrDefault(x => x is not null);
            result.Subtitles.Add(new MediaTrackDto
            {
                Type = "subtitle",
                Codec = Path.GetExtension(subtitle).TrimStart('.').ToLowerInvariant(),
                Language = language,
                Title = Path.GetFileName(subtitle),
                IsForced = tokens.Any(x => x.Equals("forced", StringComparison.OrdinalIgnoreCase)),
                IsExternal = true
            });
        }

        return result;
    }

    private static string Clean(string value)
    {
        var clean = value.Replace('\r', ' ').Replace('\n', ' ').Trim();
        return clean.Length > 500 ? clean[..500] : clean;
    }

    private static readonly HashSet<string> LanguageFilenameTokens = new(StringComparer.OrdinalIgnoreCase)
    {
        "en", "eng", "english", "ja", "jpn", "japanese", "es", "spa", "spanish",
        "fr", "fre", "fra", "french", "de", "deu", "ger", "german", "it", "ita", "italian",
        "pt", "por", "portuguese", "ko", "kor", "korean", "zh", "zho", "chi", "chinese",
        "ru", "rus", "russian", "ar", "ara", "arabic", "hi", "hin", "hindi"
    };

    private static bool IsLanguageFilenameToken(string token) => LanguageFilenameTokens.Contains(token);

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch { }
    }

    private static bool IsYes(string? value) => value is not null
        && (value.Equals("yes", StringComparison.OrdinalIgnoreCase)
            || value.Equals("true", StringComparison.OrdinalIgnoreCase) || value == "1");

    private sealed class MediaInfoDocument
    {
        [JsonPropertyName("media")] public MediaInfoMedia? Media { get; set; }
    }

    private sealed class MediaInfoMedia
    {
        [JsonPropertyName("track")] public List<MediaInfoStream> Tracks { get; set; } = new();
    }

    private sealed class MediaInfoStream
    {
        [JsonPropertyName("@type")] public string? Type { get; set; }
        [JsonPropertyName("Format")] public string? Format { get; set; }
        [JsonPropertyName("CodecID")] public string? CodecId { get; set; }
        [JsonPropertyName("Language")] public string? Language { get; set; }
        [JsonPropertyName("Title")] public string? Title { get; set; }
        [JsonPropertyName("Default")] public string? Default { get; set; }
        [JsonPropertyName("Forced")] public string? Forced { get; set; }
    }
}
