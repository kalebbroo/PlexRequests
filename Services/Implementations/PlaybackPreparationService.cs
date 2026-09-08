using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IPlaybackPreparationService
{
    Task<PlaybackPreparationTaskDto?> ClaimAsync(string workerId, CancellationToken ct);
    Task<bool> ReportAsync(PlaybackPreparationReportDto report, CancellationToken ct);
}

/// <summary>Durable handoff for normalizing library files imported before physical preferred-track ordering
/// shipped. Only the latest audit row for a destination can be claimed, so replacement history cannot make
/// two workers rewrite the same Plex file.</summary>
public sealed class PlaybackPreparationService(AppDbContext db, ILogger<PlaybackPreparationService> logger)
    : IPlaybackPreparationService
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan ClaimTimeout = TimeSpan.FromHours(2);
    private static readonly FulfillmentStatus[] ActiveJobs =
        [FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading, FulfillmentStatus.Deferred];

    public async Task<PlaybackPreparationTaskDto?> ClaimAsync(string workerId, CancellationToken ct)
    {
        workerId = Trim(workerId, 128) ?? "downloader";
        var now = DateTime.UtcNow;
        var staleBefore = now - ClaimTimeout;

        // The conditional update is the ownership boundary. If another worker wins after both selected the
        // same id, only one update succeeds and the loser retries rather than touching the same file.
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var id = await db.ImportedFiles.AsNoTracking()
                .Where(file => file.FileType == "video"
                    && EF.Functions.Like(file.DestinationPath, "%.mkv")
                    && file.PlaybackPreparedAt == null
                    && file.PlaybackPreparationAttempts < MaxAttempts
                    && (file.PlaybackPreparationClaimedAt == null
                        || file.PlaybackPreparationClaimedAt < staleBefore)
                    && !db.ImportedFiles.Any(later => later.DestinationPath == file.DestinationPath
                        && later.Id > file.Id)
                    && !db.FulfillmentJobs.Any(active => active.MediaRequestId == file.FulfillmentJob!.MediaRequestId
                        && ActiveJobs.Contains(active.Status)))
                .OrderBy(file => file.ImportedAt)
                .ThenBy(file => file.Id)
                .Select(file => file.Id)
                .FirstOrDefaultAsync(ct);
            if (id == 0) return null;

            var claimed = await db.ImportedFiles
                .Where(file => file.Id == id
                    && file.PlaybackPreparedAt == null
                    && (file.PlaybackPreparationClaimedAt == null
                        || file.PlaybackPreparationClaimedAt < staleBefore)
                    && !db.ImportedFiles.Any(later => later.DestinationPath == file.DestinationPath
                        && later.Id > file.Id))
                .ExecuteUpdateAsync(update => update
                    .SetProperty(file => file.PlaybackPreparationClaimedAt, now)
                    .SetProperty(file => file.PlaybackPreparationClaimedBy, workerId), ct);
            if (claimed == 0) continue;

            var row = await db.ImportedFiles.AsNoTracking()
                .Include(file => file.FulfillmentJob)
                .ThenInclude(job => job!.MediaRequest)
                .SingleAsync(file => file.Id == id, ct);
            var job = row.FulfillmentJob!;
            return new PlaybackPreparationTaskDto
            {
                ImportedFileId = row.Id,
                DestinationPath = row.DestinationPath,
                Title = job.Title,
                MediaType = job.MediaType,
                IsAnime = job.IsAnime || job.MediaRequest?.IsAnime == true
                    || job.MediaType == MediaType.Anime,
                Policy = await PolicyAsync(job, ct)
            };
        }

        return null;
    }

    public async Task<bool> ReportAsync(PlaybackPreparationReportDto report, CancellationToken ct)
    {
        if (report.ImportedFileId <= 0 || string.IsNullOrWhiteSpace(report.WorkerId)) return false;
        var workerId = Trim(report.WorkerId, 128);
        var row = await db.ImportedFiles.FirstOrDefaultAsync(file => file.Id == report.ImportedFileId
            && file.PlaybackPreparationClaimedBy == workerId, ct);
        if (row is null) return false;

        row.PlaybackPreparationClaimedAt = null;
        row.PlaybackPreparationClaimedBy = null;
        row.PlaybackPreparationDetail = Trim(report.Detail, 2000);
        if (report.Completed)
        {
            row.PlaybackPreparedAt = DateTime.UtcNow;
            if (report.MediaTracks is not null)
                row.MediaTracksJson = JsonSerializer.Serialize(report.MediaTracks);
        }
        else if (report.Attempted)
        {
            row.PlaybackPreparationAttempts++;
            if (row.PlaybackPreparationAttempts >= MaxAttempts)
                logger.LogWarning("Playback preparation exhausted for imported file {FileId}: {Detail}",
                    row.Id, row.PlaybackPreparationDetail);
        }

        await db.SaveChangesAsync(ct);
        return true;
    }

    private async Task<MediaLanguagePolicyDto> PolicyAsync(FulfillmentJobEntity job,
        CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(job.MediaLanguagePolicyJson))
        {
            try
            {
                var policy = JsonSerializer.Deserialize<MediaLanguagePolicyDto>(job.MediaLanguagePolicyJson);
                if (policy is not null) return policy;
            }
            catch (JsonException) { }
        }

        // Jobs old enough to need this backfill may predate immutable language snapshots. Honor their
        // assigned profile (or the installation default) instead of silently imposing English on an admin
        // whose normal library language is different.
        var profileId = job.QualityProfileId ?? job.MediaRequest?.QualityProfileId;
        var profile = profileId is int id
            ? await db.QualityProfiles.AsNoTracking().FirstOrDefaultAsync(item => item.Id == id, ct)
            : await db.QualityProfiles.AsNoTracking().FirstOrDefaultAsync(item => item.IsDefault, ct);
        if (profile is not null)
        {
            return new MediaLanguagePolicyDto
            {
                Preference = profile.LanguagePreference,
                PreferredAudioLanguage = MediaLanguagePolicy.Normalize(profile.PreferredAudioLanguage),
                PreferredSubtitleLanguage = MediaLanguagePolicy.Normalize(profile.PreferredSubtitleLanguage),
                PreferForcedSubtitles = profile.PreferForcedSubtitles,
                RequiredAudioLanguages = MediaLanguagePolicy.ParseCsv(profile.RequiredAudioLanguagesCsv),
                AllowedAudioLanguages = MediaLanguagePolicy.ParseCsv(profile.AllowedLanguagesCsv),
                RequiredSubtitleLanguages = MediaLanguagePolicy.ParseCsv(profile.RequiredSubtitleLanguagesCsv),
                RequireForcedSubtitle = profile.RequireForcedSubtitle,
                AllowUnknownTrackLanguage = profile.AllowUnknownTrackLanguage,
                SetPreferredTracksAsDefault = profile.SetPreferredTracksAsDefault
            };
        }
        return MediaLanguagePolicy.SmartDefault();
    }

    private static string? Trim(string? value, int max) => string.IsNullOrWhiteSpace(value)
        ? null
        : value.Trim()[..Math.Min(max, value.Trim().Length)];
}
