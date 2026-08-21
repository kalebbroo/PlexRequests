using System.Globalization;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>Strictly maps album and track metadata from any catalog to YouTube Music. It accepts only an
/// exact title/artist identity plus an equivalent track contract, preventing a similarly named release,
/// live recording, or incomplete edition from being downloaded for the canonical request.</summary>
public sealed class YouTubeMusicDirectAcquisitionResolver(
    IMediaMetadataProvider metadata,
    ILogger<YouTubeMusicDirectAcquisitionResolver> logger) : IMusicDirectAcquisitionResolver
{
    private const string Provider = "youtube";
    private static readonly TimeSpan DurationTolerance = TimeSpan.FromSeconds(8);

    public async Task<DirectAudioAcquisitionContextDto?> ResolveAsync(MediaRef canonical,
        MediaDetailDto detail, CancellationToken ct = default)
    {
        if (canonical.MediaType != MediaType.Music
            || canonical.Kind is not (MediaKind.Album or MediaKind.Track)
            || detail.Music?.Tracks.Count is not > 0)
            return null;

        try
        {
            if (canonical.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase))
                return Build(canonical.Id, detail, canonical.Kind);

            var artist = detail.Music.ArtistCredit;
            if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(detail.Title)) return null;
            var rows = await metadata.SearchAsync(new MediaSearchQuery
            {
                Query = $"{artist} {detail.Title}",
                MediaType = MediaType.Music,
                Kind = canonical.Kind,
                Provider = Provider,
                Page = 1,
                PageSize = 10
            });

            foreach (var row in rows.Where(x => Same(x.Title, detail.Title)))
            {
                ct.ThrowIfCancellationRequested();
                var candidateRef = row.ResolveMediaRef();
                if (!candidateRef.IsValid || !candidateRef.Provider.Equals(Provider, StringComparison.OrdinalIgnoreCase)
                    || candidateRef.Kind != canonical.Kind) continue;
                var candidate = await metadata.GetDetailsAsync(candidateRef);
                if (candidate is null || !Equivalent(detail, candidate, canonical.Kind)) continue;
                var resolved = Build(candidateRef.Id, candidate, canonical.Kind);
                if (resolved is not null) return resolved;
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            logger.LogDebug(ex, "YouTube Music direct mapping deferred for {Identity}", canonical.StableKey);
        }
        return null;
    }

    private static bool Equivalent(MediaDetailDto expected, MediaDetailDto candidate, MediaKind kind)
    {
        if (!Same(expected.Title, candidate.Title)
            || !Same(expected.Music?.ArtistCredit, candidate.Music?.ArtistCredit)) return false;
        if (kind == MediaKind.Track)
        {
            if (!string.IsNullOrWhiteSpace(expected.Music?.AlbumTitle)
                && !string.IsNullOrWhiteSpace(candidate.Music?.AlbumTitle)
                && !Same(expected.Music.AlbumTitle, candidate.Music.AlbumTitle)) return false;
            var expectedDuration = expected.Music?.Tracks.FirstOrDefault()?.DurationMs;
            var candidateDuration = candidate.Music?.Tracks.FirstOrDefault()?.DurationMs;
            return expectedDuration is not > 0 || candidateDuration is not > 0
                   || Math.Abs(expectedDuration.Value - candidateDuration.Value) <= DurationTolerance.TotalMilliseconds;
        }

        var wanted = TrackMultiset(expected.Music?.Tracks);
        var found = TrackMultiset(candidate.Music?.Tracks);
        return wanted.Count > 0 && wanted.Count == found.Count
            && wanted.All(x => found.TryGetValue(x.Key, out var count) && count == x.Value);
    }

    private static DirectAudioAcquisitionContextDto? Build(string externalId, MediaDetailDto detail, MediaKind kind)
    {
        var tracks = detail.Music?.Tracks
            .Where(x => IsVideoId(x.RecordingId))
            .OrderBy(x => Math.Max(1, x.DiscNumber)).ThenBy(x => Math.Max(1, x.TrackNumber))
            .Select(CloneTrack).ToList() ?? new();
        var expected = kind == MediaKind.Track ? 1 : Math.Max(detail.Music?.TrackCount ?? 0,
            detail.Music?.Tracks.Count ?? 0);
        if (kind == MediaKind.Track) tracks = tracks.Take(1).ToList();
        return tracks.Count == expected && expected > 0
            ? new DirectAudioAcquisitionContextDto
            {
                Provider = Provider,
                ExternalId = externalId,
                Tracks = tracks
            }
            : null;
    }

    private static MusicTrackMetadataDto CloneTrack(MusicTrackMetadataDto track) => new()
    {
        RecordingId = track.RecordingId,
        Title = track.Title,
        ArtistCredit = track.ArtistCredit,
        DiscNumber = Math.Max(1, track.DiscNumber),
        TrackNumber = Math.Max(1, track.TrackNumber),
        DurationMs = track.DurationMs
    };

    private static Dictionary<string, int> TrackMultiset(IEnumerable<MusicTrackMetadataDto>? tracks) =>
        (tracks ?? []).Select(x => Normalize(x.Title)).Where(x => x.Length > 0)
        .GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);

    private static bool Same(string? left, string? right)
    {
        var a = Normalize(left);
        var b = Normalize(right);
        return a.Length > 0 && a == b;
    }

    private static string Normalize(string? value) => new((value ?? string.Empty)
        .Normalize(System.Text.NormalizationForm.FormD)
        .Where(c => char.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
        .Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());

    private static bool IsVideoId(string? value) => value is { Length: 11 }
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-');
}
