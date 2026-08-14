using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Implementations;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Re-derives quality tier and custom-format score for the files already in the library, from the release
/// names stored at import time.
///
/// Without this, a scoring rule only ever applies to things downloaded after it was written: edit a format
/// or change its score and the entire existing library keeps whatever score it happened to get, so
/// "minimum score" and the format side of the cutoff mean nothing for anything already held. That is the
/// difference between a scoring system and a scoring system that works.
///
/// It also upgrades the tier: <c>FulfillmentQueue.RecomputeAchievedQualityAsync</c> can only land a file on
/// the Unknown-SOURCE tier for its resolution, because at that point all it has is a pixel height. The
/// release name has the source in it, so a WEBDL-1080p file stops being recorded as a generic 1080p one —
/// and since tiers are (resolution x source), that is what makes a cutoff of "WEBDL-1080p" enforceable
/// against the back catalogue.
///
/// Everything it needs is already in the database. It never touches an indexer and never re-downloads.
/// </summary>
public class RecomputeFormatScoresJob(
    AppDbContext db,
    ICustomFormatService formats,
    ILogger<RecomputeFormatScoresJob> logger) : IJobHandler
{
    public JobType Type => JobType.RecomputeFormatScores;

    /// <summary>Bounded per run so a large library doesn't hold a scoped DbContext open for minutes.</summary>
    private const int MaxFilesPerRun = 5000;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        var parser = new ReleaseParser();

        // Resume point. Without it the cap would take the same first N rows every single run and everything
        // past them would never be re-scored at all — which would quietly defeat the one thing this job is
        // for on exactly the large libraries that need it most.
        var cursor = ReadCursor(context.Schedule);

        // Files worth looking at: a stored release name is the whole input. Rows imported before the name
        // was recorded can't be re-derived and are skipped rather than guessed at.
        var files = await db.ImportedFiles
            .Where(f => f.Id > cursor && f.FileType == "video" && f.ReleaseName != null && f.ReleaseName != "")
            .OrderBy(f => f.Id)
            .Take(MaxFilesPerRun)
            .ToListAsync(ct);

        if (files.Count == 0)
        {
            // End of the library: rewind so the next pass re-scores from the start, which is what makes a
            // format edit reach everything rather than only what came after the cursor.
            WriteCursor(context.Schedule, 0);
            return cursor == 0
                ? JobResult.Skipped("No imported files carry a release name yet")
                : JobResult.Skipped($"Reached the end of the library at file #{cursor}; the next pass starts over");
        }

        // A full batch means there's more to come; a short one means this pass finished the library.
        WriteCursor(context.Schedule, files.Count == MaxFilesPerRun ? files[^1].Id : 0);

        // job id -> request id, so each file can be scored against ITS request's profile. Scores are
        // per-profile by design, so scoring everything against one profile would be wrong for any library
        // with more than one.
        var jobIds = files.Select(f => f.FulfillmentJobId).Distinct().ToList();
        var requestByJob = await db.FulfillmentJobs
            .Where(j => jobIds.Contains(j.Id))
            .Select(j => new { j.Id, j.MediaRequestId })
            .ToDictionaryAsync(x => x.Id, x => x.MediaRequestId, ct);

        var requestIds = requestByJob.Values.Distinct().ToList();
        var profileByRequest = await db.MediaRequests
            .Where(r => requestIds.Contains(r.Id))
            .Select(r => new { r.Id, r.QualityProfileId })
            .ToDictionaryAsync(x => x.Id, x => x.QualityProfileId, ct);

        var defaultProfileId = await db.QualityProfiles.Where(p => p.IsDefault)
            .Select(p => (int?)p.Id).FirstOrDefaultAsync(ct);

        var allFormats = await formats.GetAllAsync();
        var scoresByProfile = new Dictionary<int, Dictionary<int, int>>();
        var definitions = await db.QualityDefinitions.AsNoTracking()
            .Select(d => new { d.Id, d.Resolution, d.Source }).ToListAsync(ct);

        var changedFiles = 0;
        var touchedRequests = new HashSet<int>();

        foreach (var file in files)
        {
            if (ct.IsCancellationRequested) break;

            var parsed = parser.Parse(file.ReleaseName!);
            var candidate = new ReleaseCandidate { ReleaseName = file.ReleaseName!, Magnet = string.Empty };

            var requestId = requestByJob.TryGetValue(file.FulfillmentJobId, out var rid) ? rid : 0;
            var profileId = (requestId != 0 && profileByRequest.TryGetValue(requestId, out var pid) ? pid : null)
                            ?? defaultProfileId;

            var score = 0;
            if (profileId is int p)
            {
                if (!scoresByProfile.TryGetValue(p, out var scores))
                    scoresByProfile[p] = scores = await formats.ScoresForProfileAsync(p);
                (score, _) = CustomFormatMatcher.Score(parsed, candidate, allFormats, scores);
            }

            var changed = false;
            if (file.CustomFormatScore != score) { file.CustomFormatScore = score; changed = true; }

            // Only ever move a file to a MORE specific tier. The name is evidence of the source; the absence
            // of one in the name is not evidence that a previously-identified source was wrong.
            if (parsed.Resolution > 0 && parsed.Source != ReleaseSource.Unknown)
            {
                var defId = definitions
                    .FirstOrDefault(d => d.Resolution == parsed.Resolution && d.Source == parsed.Source)?.Id;
                if (defId is int id && file.QualityDefinitionId != id) { file.QualityDefinitionId = id; changed = true; }
            }

            // A height of 0 means the importer couldn't tell — the name usually can, and an unknown height is
            // what leaves a request's cutoff state stuck at Unknown forever.
            if (file.ResolutionHeight == 0 && parsed.Resolution > 0)
            {
                file.ResolutionHeight = parsed.Resolution;
                changed = true;
            }

            if (changed)
            {
                changedFiles++;
                if (requestId != 0) touchedRequests.Add(requestId);
            }
        }

        if (changedFiles == 0) return JobResult.Skipped($"Re-scored {files.Count} file(s); nothing changed");

        await db.SaveChangesAsync(ct);

        // Roll the file-level scores up onto the requests. Worst-file-wins, the same rule achieved quality
        // uses: one badly-scoring episode should drag a season down, not be averaged away.
        var rolledUp = await RollUpAsync(touchedRequests, ct);

        logger.LogInformation(
            "Re-scored {Files} imported file(s) from stored release names; {Changed} changed, {Requests} request(s) updated",
            files.Count, changedFiles, rolledUp);

        return JobResult.Ok(changedFiles,
            $"Re-derived {changedFiles} of {files.Count} file(s) and refreshed {rolledUp} request(s)");
    }

    // The schedule row's free-form payload is the natural home for the cursor: it's per-job, already
    // persisted, and already handed to the handler. Malformed or absent means "start from the beginning",
    // which is always safe — re-scoring a file is idempotent.
    private static long ReadCursor(ScheduledJobEntity? schedule)
    {
        if (string.IsNullOrWhiteSpace(schedule?.PayloadJson)) return 0;
        try
        {
            using var doc = JsonDocument.Parse(schedule.PayloadJson);
            return doc.RootElement.TryGetProperty("lastFileId", out var v) && v.TryGetInt64(out var id) ? id : 0;
        }
        catch { return 0; }
    }

    private static void WriteCursor(ScheduledJobEntity? schedule, long lastFileId)
    {
        // Null when run ad hoc rather than from the schedule — that pass just doesn't advance the cursor.
        if (schedule is null) return;
        schedule.PayloadJson = $"{{\"lastFileId\":{lastFileId}}}";
    }

    private async Task<int> RollUpAsync(HashSet<int> requestIds, CancellationToken ct)
    {
        var updated = 0;
        foreach (var requestId in requestIds)
        {
            if (ct.IsCancellationRequested) break;

            var request = await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId, ct);
            if (request is null) continue;

            var jobIds = await db.FulfillmentJobs.Where(j => j.MediaRequestId == requestId)
                .Select(j => j.Id).ToListAsync(ct);
            var scores = await db.ImportedFiles
                .Where(f => jobIds.Contains(f.FulfillmentJobId) && f.FileType == "video" && f.ReleaseName != null)
                .Select(f => f.CustomFormatScore)
                .ToListAsync(ct);
            if (scores.Count == 0) continue;

            var achieved = scores.Min();
            if (request.AchievedFormatScore == achieved) continue;

            request.AchievedFormatScore = achieved;

            // The format side of the cutoff: a profile can require a score as well as a tier, and a request
            // that no longer clears it becomes an upgrade candidate again. Only ever moves Met -> Unmet here;
            // deciding that a request IS met is the quality rollup's job, which knows about resolutions.
            if (request.QualityProfileId is int profileId)
            {
                var cutoffScore = await db.QualityProfiles.Where(p => p.Id == profileId)
                    .Select(p => p.CutoffFormatScore).FirstOrDefaultAsync(ct);
                if (cutoffScore != 0 && achieved < cutoffScore && request.CutoffState == CutoffState.Met)
                    request.CutoffState = CutoffState.Unmet;
            }

            updated++;
        }

        if (updated > 0) await db.SaveChangesAsync(ct);
        return updated;
    }
}
