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
    private static readonly TimeSpan RemuxTimeout = TimeSpan.FromHours(1);

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
            logger.LogWarning("Automatic playback ordering is not supported for {Extension}; preserving release order for {File}",
                sourceExtension, Path.GetFileName(stagedPath));
            return false;
        }

        var before = Identify(stagedPath);
        var plan = MkvTrackPlan.Create(before, selection);
        if (plan.RequiresRemux)
            Remux(stagedPath, plan);
        else
            EditFlags(stagedPath, selection);

        var after = Identify(stagedPath);
        VerifyPrepared(before, after, plan, selection, stagedPath);
        logger.LogInformation("Prepared playback order for {File}: audio {Audio}, subtitle {Subtitle}, remuxed={Remuxed}",
            Path.GetFileName(stagedPath), selection.AudioOrdinal?.ToString() ?? "unchanged",
            selection.EditSubtitles ? selection.SubtitleOrdinal?.ToString() ?? "off" : "unchanged",
            plan.RequiresRemux);
        return true;
    }

    private static void EditFlags(string stagedPath, MediaTrackDefaultSelection selection)
    {
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
        Run(start, stagedPath, TimeSpan.FromMinutes(2), "mkvpropedit");
    }

    private static void Remux(string stagedPath, MkvTrackPlan plan)
    {
        var directory = Path.GetDirectoryName(stagedPath) ?? Directory.GetCurrentDirectory();
        var remuxPath = Path.Combine(directory,
            $".{Path.GetFileNameWithoutExtension(stagedPath)}.plexrequests-remux-{Guid.NewGuid():N}.mkv");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "mkvmerge",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(remuxPath);
            start.ArgumentList.Add("--track-order");
            start.ArgumentList.Add(string.Join(',', plan.OrderedTrackIds.Select(id => $"0:{id}")));
            foreach (var track in plan.Source.Tracks)
            {
                start.ArgumentList.Add("--default-track-flag");
                start.ArgumentList.Add($"{track.Id}:{(plan.DefaultTrackIds.Contains(track.Id) ? 1 : 0)}");
            }
            start.ArgumentList.Add(stagedPath);

            Run(start, stagedPath, RemuxTimeout, "mkvmerge");
            if (!File.Exists(remuxPath) || new FileInfo(remuxPath).Length <= 0)
                throw new InvalidOperationException($"mkvmerge produced no usable output for '{Path.GetFileName(stagedPath)}'");

            var candidate = Identify(remuxPath);
            VerifyPrepared(plan.Source, candidate, plan, plan.Selection, stagedPath);
            File.Move(remuxPath, stagedPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(remuxPath)) File.Delete(remuxPath);
        }
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

    private static void Run(ProcessStartInfo start, string path, TimeSpan timeout, string tool)
    {
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException($"{tool} did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException($"{tool} is unavailable; playback ordering could not be applied", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)timeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"{tool} exceeded {timeout.TotalMinutes:F0} minutes for '{Path.GetFileName(path)}'");
        }
        Task.WaitAll(stdout, stderr);
        // MKVToolNix uses 1 for a warning and 2 for a hard error.
        if (process.ExitCode > 1)
            throw new InvalidOperationException($"{tool} could not update '{Path.GetFileName(path)}': {Clean(stderr.Result)}");
    }

    private static MkvProbe Identify(string path)
    {
        var start = new ProcessStartInfo
        {
            FileName = "mkvmerge",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        start.ArgumentList.Add("--identify");
        start.ArgumentList.Add("--identification-format");
        start.ArgumentList.Add("json");
        start.ArgumentList.Add(path);

        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new InvalidOperationException("mkvmerge did not start");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            throw new InvalidOperationException("mkvmerge is unavailable; playback ordering could not be verified", ex);
        }
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit((int)ProbeTimeout.TotalMilliseconds))
        {
            TryKill(process);
            throw new TimeoutException($"mkvmerge identification exceeded {ProbeTimeout.TotalSeconds:F0} seconds for '{Path.GetFileName(path)}'");
        }
        Task.WaitAll(stdout, stderr);
        if (process.ExitCode > 1)
            throw new InvalidOperationException($"mkvmerge could not identify '{Path.GetFileName(path)}': {Clean(stderr.Result)}");
        try { return MkvProbe.Parse(stdout.Result); }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"mkvmerge returned invalid metadata for '{Path.GetFileName(path)}'", ex);
        }
    }

    private static void VerifyPrepared(MkvProbe before, MkvProbe after, MkvTrackPlan plan,
        MediaTrackDefaultSelection selection, string path)
    {
        if (after.Tracks.Count != before.Tracks.Count
            || after.Attachments != before.Attachments
            || after.Chapters != before.Chapters
            || after.GlobalTags != before.GlobalTags
            || after.TrackTags != before.TrackTags)
            throw new InvalidOperationException($"Playback remux verification failed for '{Path.GetFileName(path)}': container inventory changed");

        var expected = plan.OrderedTrackIds.Select(id => before.Tracks.Single(track => track.Id == id)).ToList();
        if (!expected.Select(Identity).SequenceEqual(after.Tracks.Select(Identity)))
            throw new InvalidOperationException($"Playback remux verification failed for '{Path.GetFileName(path)}': stream order or identity changed");

        if (selection.AudioOrdinal is not null)
            VerifyDefaults(after.Audio, 1, "audio", path);
        if (selection.EditSubtitles)
            VerifyDefaults(after.Subtitles, selection.SubtitleOrdinal is null ? null : 1, "subtitle", path);
    }

    private static string Identity(MkvTrack track) =>
        $"{track.Type}\u001f{MediaLanguagePolicy.Normalize(track.Language)}\u001f{track.Codec}";

    private static void VerifyDefaults(IReadOnlyList<MkvTrack> tracks, int? selectedOrdinal,
        string type, string path)
    {
        var defaults = tracks.Select((track, index) => (track, ordinal: index + 1))
            .Where(x => x.track.IsDefault).Select(x => x.ordinal).ToList();
        if (selectedOrdinal is int ordinal ? defaults.Count != 1 || defaults[0] != ordinal : defaults.Count != 0)
            throw new InvalidOperationException($"Playback remux verification failed for '{Path.GetFileName(path)}': {type} default is incorrect");
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

internal sealed record MkvTrack(int Id, string Type, string? Codec, string? Language, bool IsDefault);

internal sealed record MkvProbe(
    IReadOnlyList<MkvTrack> Tracks,
    int Attachments,
    int Chapters,
    int GlobalTags,
    int TrackTags)
{
    public IReadOnlyList<MkvTrack> Audio => Tracks.Where(track => track.Type == "audio").ToList();
    public IReadOnlyList<MkvTrack> Subtitles => Tracks.Where(track => track.Type == "subtitle").ToList();

    public static MkvProbe Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("container", out var container))
        {
            if (container.TryGetProperty("recognized", out var recognized)
                && recognized.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("mkvmerge did not recognize the staged Matroska container");
            if (container.TryGetProperty("supported", out var supported)
                && supported.ValueKind == JsonValueKind.False)
                throw new InvalidOperationException("mkvmerge cannot remux the staged Matroska container");
        }

        if (!root.TryGetProperty("tracks", out var trackElements)
            || trackElements.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException("mkvmerge did not return a track inventory");

        var tracks = new List<MkvTrack>();
        foreach (var element in trackElements.EnumerateArray())
        {
            var id = element.GetProperty("id").GetInt32();
            var rawType = element.GetProperty("type").GetString() ?? string.Empty;
            var type = rawType.Equals("subtitles", StringComparison.OrdinalIgnoreCase)
                ? "subtitle"
                : rawType.ToLowerInvariant();
            var codec = element.TryGetProperty("codec", out var codecElement)
                ? codecElement.GetString()
                : null;
            string? language = null;
            var isDefault = false;
            if (element.TryGetProperty("properties", out var properties))
            {
                if (properties.TryGetProperty("language_ietf", out var ietf)) language = ietf.GetString();
                if (string.IsNullOrWhiteSpace(language)
                    && properties.TryGetProperty("language", out var legacy)) language = legacy.GetString();
                if (properties.TryGetProperty("default_track", out var defaultTrack)
                    && defaultTrack.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    isDefault = defaultTrack.GetBoolean();
            }
            tracks.Add(new MkvTrack(id, type, codec, language, isDefault));
        }

        if (tracks.Select(track => track.Id).Distinct().Count() != tracks.Count)
            throw new InvalidOperationException("mkvmerge returned duplicate track identifiers");
        return new MkvProbe(tracks,
            ArrayLength(root, "attachments"),
            ArrayLength(root, "chapters"),
            ArrayLength(root, "global_tags"),
            ArrayLength(root, "track_tags"));
    }

    private static int ArrayLength(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.Array
            ? element.GetArrayLength()
            : 0;
}

internal sealed record MkvTrackPlan(
    MkvProbe Source,
    MediaTrackDefaultSelection Selection,
    IReadOnlyList<int> OrderedTrackIds,
    IReadOnlySet<int> DefaultTrackIds,
    bool RequiresRemux)
{
    public static MkvTrackPlan Create(MkvProbe source, MediaTrackDefaultSelection selection)
    {
        if (source.Audio.Count != selection.AudioCount || source.Subtitles.Count != selection.SubtitleCount)
            throw new InvalidOperationException(
                $"MediaInfo/MKVToolNix stream inventory mismatch: expected {selection.AudioCount} audio and {selection.SubtitleCount} subtitle tracks, found {source.Audio.Count} and {source.Subtitles.Count}");

        var audio = Selected(source.Audio, selection.AudioOrdinal, "audio");
        var subtitle = selection.EditSubtitles
            ? Selected(source.Subtitles, selection.SubtitleOrdinal, "subtitle")
            : null;
        var ordered = source.Tracks.Select(track => track.Id).ToList();
        MoveBeforeFirstType(ordered, source, audio);
        MoveBeforeFirstType(ordered, source, subtitle);

        var defaults = source.Tracks.Where(track => track.IsDefault).Select(track => track.Id).ToHashSet();
        if (selection.AudioOrdinal is not null)
        {
            defaults.ExceptWith(source.Audio.Select(track => track.Id));
            defaults.Add(audio!.Id);
        }
        if (selection.EditSubtitles)
        {
            defaults.ExceptWith(source.Subtitles.Select(track => track.Id));
            if (subtitle is not null) defaults.Add(subtitle.Id);
        }

        var original = source.Tracks.Select(track => track.Id);
        return new MkvTrackPlan(source, selection, ordered, defaults, !ordered.SequenceEqual(original));
    }

    private static MkvTrack? Selected(IReadOnlyList<MkvTrack> tracks, int? ordinal, string type)
    {
        if (ordinal is null) return null;
        if (ordinal < 1 || ordinal > tracks.Count)
            throw new InvalidOperationException($"Selected {type} track {ordinal} is outside the inspected inventory");
        return tracks[ordinal.Value - 1];
    }

    private static void MoveBeforeFirstType(List<int> order, MkvProbe source, MkvTrack? selected)
    {
        if (selected is null) return;
        var first = source.Tracks.First(track => track.Type == selected.Type).Id;
        if (selected.Id == first) return;
        order.Remove(selected.Id);
        order.Insert(order.IndexOf(first), selected.Id);
    }
}
