using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Jobs;

/// <summary>
/// Repairs the approval-to-queue handoff when the request status committed but no fulfillment job ever did.
/// The query deliberately requires zero job history: active/deferred work, completed history, and failures
/// belong to their existing retry/state machines and must not be duplicated here.
/// </summary>
public sealed class ApprovedRequestRepairJob(
    AppDbContext db,
    IFulfillmentQueue fulfillment,
    IConfiguration config,
    ILogger<ApprovedRequestRepairJob> logger) : IJobHandler
{
    private const int MaxRepairsPerRun = 100;

    public JobType Type => JobType.ApprovedRequestRepair;

    public async Task<JobResult> ExecuteAsync(JobContext context, CancellationToken ct)
    {
        if (!config.GetValue<bool>("Fulfillment:Enabled"))
            return JobResult.Skipped("Fulfillment is disabled");

        var repaired = await RunOnceAsync(ct);
        return repaired > 0
            ? JobResult.Ok(repaired, $"Recreated {repaired} missing fulfillment job(s)")
            : JobResult.Skipped("No Approved requests were missing fulfillment jobs");
    }

    internal async Task<int> RunOnceAsync(CancellationToken ct)
    {
        var orphaned = await db.MediaRequests.AsNoTracking()
            .Where(request => request.Status == RequestStatus.Approved
                && !db.FulfillmentJobs.Any(job => job.MediaRequestId == request.Id))
            .OrderBy(request => request.RequestedAt)
            .Take(MaxRepairsPerRun)
            .ToListAsync(ct);

        var repaired = 0;
        foreach (var request in orphaned)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (!await fulfillment.EnqueueAsync(ToDto(request))) continue;
                repaired++;
                logger.LogWarning(
                    "Repaired Approved request {RequestId} {Title}: created its missing fulfillment job",
                    request.Id,
                    request.Title);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One malformed legacy request must not keep later valid approvals orphaned. It remains
                // eligible for the next bounded pass after the underlying issue is corrected.
                logger.LogWarning(ex, "Could not repair Approved request {RequestId} {Title}", request.Id, request.Title);
            }
        }

        return repaired;
    }

    private static MediaRequestDto ToDto(MediaRequestEntity request) => new()
    {
        Id = request.Id,
        MediaId = request.MediaId,
        MediaType = request.MediaType,
        Title = request.Title,
        PosterUrl = request.PosterUrl,
        Status = request.Status,
        RequestedAt = request.RequestedAt,
        ApprovedAt = request.ApprovedAt,
        RequestedByUserId = request.RequestedByUserId ?? 0,
        RequestedByUsername = request.RequestedBy ?? string.Empty,
        QualityProfileId = request.QualityProfileId,
        RequestNote = request.RequestNote,
        RequestAllSeasons = request.RequestAllSeasons,
        RequestedEpisodesCsv = request.RequestedEpisodesCsv,
        RequestedSeasons = string.IsNullOrWhiteSpace(request.RequestedSeasonsCsv)
            ? new List<int>()
            : request.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(value => int.TryParse(value, out var season) ? (int?)season : null)
                .Where(season => season.HasValue)
                .Select(season => season!.Value)
                .ToList(),
        Monitored = request.Monitored,
        ExternalId = request.ExternalId,
        ExternalSource = request.ExternalSource,
        MediaRef = !string.IsNullOrWhiteSpace(request.ExternalId)
            ? MediaRef.FromExternal(request.ExternalSource ?? "external", request.ExternalId,
                request.MediaType, request.RequestScopeKind.ToMediaKind(request.MediaType))
            : MediaRef.FromTmdb(request.MediaId, request.MediaType),
        RequestScopeKind = request.RequestScopeKind
    };
}
