using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequests.Downloader.Organize;

public interface IMediaTrackInspector
{
    Task<MediaTrackSummaryDto> InspectAsync(string mediaPath, IReadOnlyList<string> companionSubtitles,
        CancellationToken ct);
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

    internal static MediaTrackSummaryDto ParseOutput(string json, IReadOnlyList<string> companionSubtitles)
    {
        var document = JsonSerializer.Deserialize<MediaInfoDocument>(json, Json) ?? new();
        var result = new MediaTrackSummaryDto();
        foreach (var stream in document.Media?.Tracks ?? [])
        {
            var type = stream.Type?.ToLowerInvariant() switch
            {
                "audio" => "audio",
                "text" or "subtitle" => "subtitle",
                _ => null
            };
            if (type is null) continue;
            var track = new MediaTrackDto
            {
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
