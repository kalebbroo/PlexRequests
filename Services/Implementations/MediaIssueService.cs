using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public interface IMediaIssueService
{
    Task<MediaIssueResultDto> ReportIssueAsync(MediaIssueReportDto report);
    /// <summary>The signed-in user's own issue history. Administrator status never broadens this view.</summary>
    Task<List<MediaIssueDto>> GetMyIssuesAsync(int take = 100);
    Task<List<MediaIssueDto>> GetIssuesAsync(bool openOnly = true);
    Task<int> GetOpenCountAsync();
    Task<bool> ResolveIssueAsync(int issueId);
    Task<bool> DismissIssueAsync(int issueId);
    Task<MediaIssueResultDto> RequestRedownloadAsync(int issueId);
}

/// <summary>
/// Owns the complete issue lifecycle. Replacement approval deliberately attaches to the existing Available
/// request: creating a second ordinary request allows Plex availability to "complete" it from the bad file
/// before a new copy arrives. The replacement job keeps that old file until its new import succeeds.
/// </summary>
public class MediaIssueService(
    AppDbContext db,
    AuthenticationStateProvider authProvider,
    IFulfillmentQueue fulfillment,
    IQualityProfileService qualityProfiles,
    IReleaseBlocklistService blocklist,
    IUserAccessService? userAccess = null,
    INotificationService? notifications = null,
    ILogger<MediaIssueService>? logger = null,
    IStorageOptimizationService? storageOptimization = null) : IMediaIssueService
{
    private static readonly HashSet<string> Reasons = new(StringComparer.OrdinalIgnoreCase)
    {
        "Wrong episode", "Bad quality", "Playback broken", "Wrong version",
        "Audio / language problem", "Audio out of sync", "Subtitle problem",
        "Missing subtitles", "Corrupt / incomplete", "Other"
    };

    public async Task<MediaIssueResultDto> ReportIssueAsync(MediaIssueReportDto report)
    {
        var user = await CurrentUserAsync();
        if (user.Identity?.IsAuthenticated != true)
            return Failed("You must be signed in to report a problem.");
        var currentUserId = UserId(user);
        if (currentUserId is int accessUserId && userAccess is not null
            && !(await userAccess.GetAccessAsync(accessUserId)).IsActive)
            return Failed("Your account is suspended or disabled.");
        if (report.MediaId <= 0 || string.IsNullOrWhiteSpace(report.Title))
            return Failed("The media item could not be identified.");

        var isTv = report.MediaType is MediaType.TvShow or MediaType.Anime;
        if (report.RequestReplacement && isTv && (report.SeasonNumber is null || report.EpisodeNumber is null))
            return Failed("Choose the exact season and episode that needs replacing.");
        if ((report.SeasonNumber is not null || report.EpisodeNumber is not null) &&
            (report.SeasonNumber is null or < 0 || report.EpisodeNumber is null or < 1))
            return Failed("Choose a valid season and episode.");
        if (!isTv && (report.SeasonNumber is not null || report.EpisodeNumber is not null))
            return Failed("Episode details can only be attached to a TV or anime report.");

        var userId = currentUserId;
        var duplicate = await db.MediaIssues.AsNoTracking().AnyAsync(i =>
            i.MediaId == report.MediaId && i.MediaType == report.MediaType &&
            i.ReportedByUserId == userId && i.SeasonNumber == report.SeasonNumber &&
            i.EpisodeNumber == report.EpisodeNumber &&
            (i.Status == IssueStatus.Open || i.Status == IssueStatus.ReplacementQueued));
        if (duplicate) return Failed("You already have an active report for this item.");

        int? importedFileId = null;
        if (isTv && report.SeasonNumber is int reportedSeason && report.EpisodeNumber is int reportedEpisode)
        {
            importedFileId = await (from file in db.ImportedFiles.AsNoTracking()
                join job in db.FulfillmentJobs.AsNoTracking() on file.FulfillmentJobId equals job.Id
                join request in db.MediaRequests.AsNoTracking() on job.MediaRequestId equals request.Id
                where request.MediaId == report.MediaId && request.MediaType == report.MediaType
                      && file.FileType == "video"
                      && ((file.SeasonNumber == reportedSeason && file.EpisodeNumber == reportedEpisode)
                          || file.EpisodeCoverage.Any(c => c.SeasonNumber == reportedSeason
                                                          && c.EpisodeNumber == reportedEpisode))
                orderby file.ImportedAt descending, file.Id descending
                select (int?)file.Id).FirstOrDefaultAsync();
        }

        var submittedReason = report.Reason?.Trim() ?? string.Empty;
        var reason = Reasons.Contains(submittedReason) ? submittedReason : "Other";
        var issue = new MediaIssueEntity
        {
            MediaId = report.MediaId,
            MediaType = report.MediaType,
            Title = Trim(report.Title, 512)!,
            PosterUrl = report.PosterUrl,
            ReportedByUserId = userId,
            ReportedBy = Trim(user.Identity.Name, 128),
            Reason = reason,
            Detail = Trim(report.Detail, 2048),
            SeasonNumber = report.SeasonNumber,
            EpisodeNumber = report.EpisodeNumber,
            EpisodeTitle = Trim(report.EpisodeTitle, 512),
            ImportedFileId = importedFileId,
            ReplacementRequested = report.RequestReplacement,
            Status = IssueStatus.Open,
            CreatedAt = DateTime.UtcNow
        };
        db.MediaIssues.Add(issue);
        await db.SaveChangesAsync();
        await SafeNotify(() => notifications!.MediaIssueReportedAsync(ToDto(issue, null, null)));

        if (report.RequestReplacement && await IsAdminAsync(user))
        {
            var queued = await QueueReplacementCoreAsync(issue, user.Identity.Name ?? "Admin");
            queued.IssueId = issue.Id;
            // The report itself was persisted even when the safe replacement preflight found a problem.
            queued.Success = true;
            return queued;
        }

        return new MediaIssueResultDto { Success = true, IssueId = issue.Id };
    }

    public async Task<List<MediaIssueDto>> GetIssuesAsync(bool openOnly = true)
    {
        if (!await IsAdminAsync()) return new();
        var query = db.MediaIssues.AsNoTracking().AsQueryable();
        if (openOnly)
            query = query.Where(i => i.Status == IssueStatus.Open || i.Status == IssueStatus.ReplacementQueued);
        var issues = await query.OrderByDescending(i => i.CreatedAt).ToListAsync();
        var jobIds = issues.Where(i => i.ReplacementJobId.HasValue).Select(i => i.ReplacementJobId!.Value).ToList();
        var jobs = await db.FulfillmentJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id);
        var fileIds = issues.Where(i => i.ImportedFileId.HasValue).Select(i => i.ImportedFileId!.Value)
            .Distinct().ToList();
        var files = await db.ImportedFiles.AsNoTracking().Where(f => fileIds.Contains(f.Id))
            .ToDictionaryAsync(f => f.Id);
        var dtos = issues.Select(i => ToDto(i,
            i.ReplacementJobId is int id && jobs.TryGetValue(id, out var job) ? job : null,
            i.ImportedFileId is int fileId && files.TryGetValue(fileId, out var file) ? file : null)).ToList();

        // Open reports are few, so each one runs the same preflight its approval runs rather than a batched copy.
        for (var index = 0; index < issues.Count; index++)
        {
            if (issues[index].Status != IssueStatus.Open) continue;
            var blockedReason = (await PreflightReplacementAsync(issues[index])).BlockedReason;
            dtos[index].CanApproveReplacement = blockedReason is null;
            dtos[index].ReplacementBlockedReason = blockedReason;
        }
        return dtos;
    }

    public async Task<List<MediaIssueDto>> GetMyIssuesAsync(int take = 100)
    {
        var user = await CurrentUserAsync();
        if (user.Identity?.IsAuthenticated != true || UserId(user) is not int userId) return new();
        if (userAccess is not null && !(await userAccess.GetAccessAsync(userId)).IsActive) return new();

        take = Math.Clamp(take, 1, 250);
        var issues = await db.MediaIssues.AsNoTracking()
            .Where(i => i.ReportedByUserId == userId)
            .OrderByDescending(i => i.CreatedAt).ThenByDescending(i => i.Id)
            .Take(take).ToListAsync();
        var jobIds = issues.Where(i => i.ReplacementJobId.HasValue).Select(i => i.ReplacementJobId!.Value)
            .Distinct().ToList();
        var jobs = await db.FulfillmentJobs.AsNoTracking().Where(j => jobIds.Contains(j.Id))
            .ToDictionaryAsync(j => j.Id);

        // Deliberately omit ImportedFileEntity here. Library paths and release internals belong to admin
        // triage; users receive their target and lifecycle state only.
        return issues.Select(i => ToDto(i,
            i.ReplacementJobId is int id && jobs.TryGetValue(id, out var job) ? job : null, null,
            includeOperationalDetails: false)).ToList();
    }

    public async Task<int> GetOpenCountAsync()
    {
        if (!await IsAdminAsync()) return 0;
        return await db.MediaIssues.CountAsync(i =>
            i.Status == IssueStatus.Open || i.Status == IssueStatus.ReplacementQueued);
    }

    public Task<bool> ResolveIssueAsync(int issueId) => CloseAsync(issueId, IssueStatus.Resolved);
    public Task<bool> DismissIssueAsync(int issueId) => CloseAsync(issueId, IssueStatus.Dismissed);

    private async Task<bool> CloseAsync(int issueId, IssueStatus status)
    {
        var user = await CurrentUserAsync();
        if (!await IsAdminAsync(user)) return false;
        var issue = await db.MediaIssues.FirstOrDefaultAsync(i => i.Id == issueId);
        if (issue is null || issue.Status == IssueStatus.ReplacementQueued) return false;
        issue.Status = status;
        issue.ResolvedAt = DateTime.UtcNow;
        issue.ResolvedBy = Trim(user.Identity?.Name, 128);
        var changed = await db.SaveChangesAsync() > 0;
        if (changed)
            await SafeNotify(() => notifications!.MediaIssueClosedAsync(ToDto(issue, null, null),
                status == IssueStatus.Dismissed));
        return changed;
    }

    public async Task<MediaIssueResultDto> RequestRedownloadAsync(int issueId)
    {
        var user = await CurrentUserAsync();
        if (!await IsAdminAsync(user)) return Failed("Administrator access is required.");
        var issue = await db.MediaIssues.FirstOrDefaultAsync(i => i.Id == issueId);
        if (issue is null) return Failed("Issue not found.");
        if (issue.Status == IssueStatus.ReplacementQueued)
            return Failed("A replacement is already queued for this issue.");
        if (issue.Status != IssueStatus.Open) return Failed("Only open issues can be approved.");
        return await QueueReplacementCoreAsync(issue, user.Identity?.Name ?? "Admin");
    }

    /// <summary>
    /// The verified inputs for one replacement approval, or the reason approval cannot run. A title with no request yet
    /// carries <see cref="InventoryKey"/> instead, and approval adopts it into the library first.
    /// </summary>
    private sealed record ReplacementPlan(MediaRequestEntity? Request, string? InventoryKey,
        List<ImportedFileEntity> OldFiles, List<string> ReplacePaths, List<long> PlexFileSizes,
        int CurrentHeight, List<(int Season, int Episode)> Episodes, string? BlockedReason)
    {
        public static ReplacementPlan Blocked(string reason) => new(null, null, [], [], [], 0, [], reason);
    }

    /// <summary>
    /// Every check an approval must pass, with no writes. The admin listing and <see cref="QueueReplacementCoreAsync"/>
    /// both run it, so a card never offers an approval that the service would refuse.
    /// </summary>
    private async Task<ReplacementPlan> PreflightReplacementAsync(MediaIssueEntity issue)
    {
        var isTv = issue.MediaType is MediaType.TvShow or MediaType.Anime;
        if (isTv && (issue.SeasonNumber is null || issue.EpisodeNumber is null))
            return ReplacementPlan.Blocked("Choose an exact episode before approving a TV replacement.");

        var candidates = await (from file in db.ImportedFiles.AsNoTracking()
            join job in db.FulfillmentJobs.AsNoTracking() on file.FulfillmentJobId equals job.Id
            join reqEntity in db.MediaRequests.AsNoTracking() on job.MediaRequestId equals reqEntity.Id
            where reqEntity.MediaId == issue.MediaId && reqEntity.MediaType == issue.MediaType && file.FileType == "video"
                  && (!isTv || (file.SeasonNumber == issue.SeasonNumber && file.EpisodeNumber == issue.EpisodeNumber)
                      || file.EpisodeCoverage.Any(c => c.SeasonNumber == issue.SeasonNumber
                                                      && c.EpisodeNumber == issue.EpisodeNumber))
            orderby file.ImportedAt descending
            select new { File = file, Job = job, Request = reqEntity }).ToListAsync();

        var current = issue.ImportedFileId is int importedFileId
            ? candidates.FirstOrDefault(x => x.File.Id == importedFileId) ?? candidates.FirstOrDefault()
            : candidates.FirstOrDefault();
        if (current is null) return await PlexLibraryPlanAsync(issue, isTv);

        var busy = await BlockingJobReasonAsync(current.Request.Id);
        if (busy is not null) return ReplacementPlan.Blocked(busy);

        var requestJobIds = await db.FulfillmentJobs.AsNoTracking()
            .Where(j => j.MediaRequestId == current.Request.Id).Select(j => j.Id).ToListAsync();
        var oldFiles = await db.ImportedFiles.AsNoTracking().Include(f => f.EpisodeCoverage)
            .Where(f => requestJobIds.Contains(f.FulfillmentJobId) &&
                (!isTv || (f.SeasonNumber == issue.SeasonNumber && f.EpisodeNumber == issue.EpisodeNumber)
                 || f.EpisodeCoverage.Any(c => c.SeasonNumber == issue.SeasonNumber
                                               && c.EpisodeNumber == issue.EpisodeNumber)))
            .ToListAsync();
        // Pin approval to the physical video that existed when the user reported the problem. Include its
        // companion audit rows, but never sweep up a newer copy of the same logical episode that arrived
        // after the report was submitted.
        oldFiles = oldFiles.Where(f => f.Id == current.File.Id ||
            (f.FileType != "video" && f.FulfillmentJobId == current.File.FulfillmentJobId &&
             string.Equals(f.TransferId, current.File.TransferId, StringComparison.OrdinalIgnoreCase))).ToList();
        var replacePaths = oldFiles.Select(f => f.DestinationPath).Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (!oldFiles.Any(f => f.FileType == "video") || replacePaths.Count == 0)
            return ReplacementPlan.Blocked("The current library file path could not be verified, so no replacement was queued.");

        // Checked before anything is blocklisted, so a refused approval leaves no side effects behind.
        if (oldFiles.Where(f => f.FileType == "video").Any(f =>
                string.IsNullOrWhiteSpace(f.InfoHash ?? f.TransferId) && string.IsNullOrWhiteSpace(f.ReleaseName)))
            return ReplacementPlan.Blocked("The bad release has no source identity, so it cannot be safely excluded from the replacement search.");

        var currentHeight = oldFiles.Where(f => f.FileType == "video").Select(f => f.ResolutionHeight).DefaultIfEmpty(0).Max();
        var episodes = new List<(int Season, int Episode)>();
        if (isTv)
        {
            episodes.AddRange(oldFiles.Where(f => f.FileType == "video")
                .SelectMany(f => f.EpisodeCoverage.Count > 0
                    ? f.EpisodeCoverage.Select(c => (c.SeasonNumber, c.EpisodeNumber))
                    : f.SeasonNumber is int season && f.EpisodeNumber is int episode
                        ? [(season, episode)]
                        : Array.Empty<(int, int)>())
                .Distinct().OrderBy(x => x.Item1).ThenBy(x => x.Item2));
            if (episodes.Count == 0)
                episodes.Add((issue.SeasonNumber!.Value, issue.EpisodeNumber!.Value));
        }
        return new ReplacementPlan(current.Request, null, oldFiles, replacePaths, [], currentHeight, episodes, null);
    }

    /// <summary>The active job that would block a new replacement for this request, described for the administrator.</summary>
    private async Task<string?> BlockingJobReasonAsync(int mediaRequestId)
    {
        var blocker = await db.FulfillmentJobs.AsNoTracking()
            .Where(j => j.MediaRequestId == mediaRequestId && FulfillmentJobPolicy.InFlightStatuses.Contains(j.Status))
            .Select(j => new { j.Id, j.Status, j.NextRetryAt })
            .FirstOrDefaultAsync();
        if (blocker is null) return null;
        var next = blocker.NextRetryAt is DateTime retry ? $", next search {retry.ToLocalTime():g}" : string.Empty;
        return $"Another download or replacement for this request is already active (job #{blocker.Id}, {blocker.Status}{next}).";
    }

    /// <summary>
    /// The fallback when there is no import audit row: the title's verified file in the Plex library. Titles that this
    /// app never downloaded, or imported before the audit table existed, have only that file to go on.
    /// </summary>
    private async Task<ReplacementPlan> PlexLibraryPlanAsync(MediaIssueEntity issue, bool isTv)
    {
        var noAudit = isTv
            ? "The existing episode has no import audit record, so Plex Requests cannot safely identify and replace its file."
            : "The existing title has no import audit record, so Plex Requests cannot safely identify and replace its file.";
        if (storageOptimization is null) return ReplacementPlan.Blocked(noAudit);

        var source = await storageOptimization.ResolvePlexReplacementSourceAsync(
            issue.MediaId, issue.MediaType, issue.SeasonNumber, issue.EpisodeNumber);
        if (source.BlockedReason is not null)
            return ReplacementPlan.Blocked($"No import record, and the Plex library has no usable file for it: {source.BlockedReason}");

        // A title that already has a live request keeps it. A cancelled or rejected one is never the target, because
        // nothing searches for it. Otherwise the request is adopted from the library at approval.
        var request = await db.MediaRequests.AsNoTracking()
            .Where(r => r.MediaId == issue.MediaId && r.MediaType == issue.MediaType
                        && r.Status != RequestStatus.Cancelled && r.Status != RequestStatus.Rejected)
            .OrderByDescending(r => r.Id).FirstOrDefaultAsync();
        if (request is not null)
        {
            var busy = await BlockingJobReasonAsync(request.Id);
            if (busy is not null) return ReplacementPlan.Blocked(busy);
        }

        var episodes = source.Episodes.ToList();
        if (isTv && episodes.Count == 0)
            episodes.Add((issue.SeasonNumber!.Value, issue.EpisodeNumber!.Value));
        return new ReplacementPlan(request, request is null ? source.InventoryKey : null, [],
            source.ManagedPaths.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), source.FileSizes.ToList(),
            source.ResolutionHeight, episodes, null);
    }

    private async Task<MediaIssueResultDto> QueueReplacementCoreAsync(MediaIssueEntity issue, string approvedBy)
    {
        var plan = await PreflightReplacementAsync(issue);
        if (plan.BlockedReason is not null)
        {
            logger?.LogInformation("Replacement for issue #{IssueId} \"{Title}\" refused: {Reason}", issue.Id, issue.Title, plan.BlockedReason);
            return Failed(plan.BlockedReason);
        }

        // A Plex-only title has no request yet. Adopt it with the same anchor the storage optimizer creates, so the
        // replacement job has a request to attach to.
        var target = plan.Request;
        if (target is null)
        {
            var adoption = await storageOptimization!.AdoptPlexTitleAsync(plan.InventoryKey!);
            if (!adoption.Success || adoption.RequestId <= 0)
                return Failed(adoption.Message ?? "Plex Requests could not connect this title to the library.");
            target = await db.MediaRequests.AsNoTracking().FirstOrDefaultAsync(r => r.Id == adoption.RequestId);
            if (target is null) return Failed("The library record for this title could not be loaded.");
        }
        var oldFiles = plan.OldFiles;
        var replacePaths = plan.ReplacePaths;

        // A Plex-only copy has no infohash or source id, so its exact size is the identity the ranker can match. It is
        // written before the job exists, so the replacement's first search already excludes those bytes.
        foreach (var size in plan.PlexFileSizes)
            await blocklist.BlockForRequestAsync(target.Id, target.MediaId, target.MediaType, new BlocklistRequestDto
            {
                SizeBytes = size,
                ReleaseName = $"Library copy of {issue.Title}",
                Reason = BlocklistReason.WrongContent,
                Detail = $"Excluded by media issue #{issue.Id}: {issue.Reason}",
                Season = issue.SeasonNumber,
                Episode = issue.EpisodeNumber
            });

        // Exclude every known source behind the bad copy before the new job is created. Otherwise the ranker
        // can select the exact same mislabeled/corrupt release on every self-healing retry.
        foreach (var file in oldFiles.Where(f => f.FileType == "video"))
        {
            var sourceId = file.InfoHash ?? file.TransferId;
            await blocklist.BlockAsync(file.FulfillmentJobId, new BlocklistRequestDto
            {
                Protocol = file.Protocol,
                SourceId = sourceId,
                InfoHash = file.InfoHash ?? (file.Protocol == AcquisitionProtocol.Torrent ? file.TransferId : null),
                ReleaseName = file.ReleaseName,
                Reason = BlocklistReason.WrongContent,
                Detail = $"Excluded by media issue #{issue.Id}: {issue.Reason}",
                Season = issue.SeasonNumber,
                Episode = issue.EpisodeNumber
            });
        }

        var profileId = target.QualityProfileId ?? await qualityProfiles.ResolveProfileIdAsync(
            issue.MediaType, issue.MediaId, genres: null, requesterChoiceId: null,
            userId: target.RequestedByUserId);
        var preferred = await qualityProfiles.GetCutoffQualityAsync(profileId);
        var currentHeight = plan.CurrentHeight;
        var existing = QualityHelper.FromHeight(currentHeight);
        var floor = (Quality)Math.Max((int)preferred, (int)existing);
        if (floor == Quality.Any) floor = Quality.HD;

        var request = ToRequestDto(target);
        request.QualityProfileId = profileId;
        var replacedHeight = plan.CurrentHeight > 0 ? plan.CurrentHeight : (int?)null;
        var jobId = await fulfillment.EnqueueReplacementAsync(request, floor, replacePaths, plan.Episodes, issue.Id,
            replacedHeight);
        if (jobId is null)
        {
            logger?.LogInformation("Replacement for issue #{IssueId} \"{Title}\" not queued: another download started first", issue.Id, issue.Title);
            return Failed("Another download or replacement started before this one could be queued.");
        }
        logger?.LogInformation(
            "Replacement for issue #{IssueId} \"{Title}\" queued as job #{JobId} on request #{RequestId} ({Source}): replacing {Paths} path(s), blocking {Sizes} size(s), floor {Floor}, {Episodes} episode(s)",
            issue.Id, issue.Title, jobId, target.Id, plan.InventoryKey is null ? "existing request" : $"adopted from {plan.InventoryKey}",
            replacePaths.Count, plan.PlexFileSizes.Count, floor, plan.Episodes.Count);

        issue.ReplacementRequested = true;
        issue.ReplacementJobId = jobId;
        issue.ReplacementApprovedAt = DateTime.UtcNow;
        issue.ReplacementApprovedBy = Trim(approvedBy, 128);
        issue.Status = IssueStatus.ReplacementQueued;
        issue.ResolvedAt = null;
        issue.ResolvedBy = null;
        await db.SaveChangesAsync();
        await SafeNotify(() => notifications!.MediaIssueApprovedAsync(ToDto(issue, job: null, file: null)));
        return new MediaIssueResultDto { Success = true, IssueId = issue.Id, ReplacementQueued = true };
    }

    private async Task<ClaimsPrincipal> CurrentUserAsync() =>
        (await authProvider.GetAuthenticationStateAsync()).User;

    private async Task<bool> IsAdminAsync() => await IsAdminAsync(await CurrentUserAsync());
    private async Task<bool> IsAdminAsync(ClaimsPrincipal user)
    {
        var userId = UserId(user);
        return userAccess is not null && userId is int id
            ? await userAccess.IsAdminAsync(id)
            : user.IsInRole("Admin");
    }
    private static int? UserId(ClaimsPrincipal user) =>
        int.TryParse(user.FindFirst("user_id")?.Value, out var id) ? id : null;
    private static string? Trim(string? value, int max)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var text = value.Trim();
        return text.Length > max ? text[..max] : text;
    }
    private static MediaIssueResultDto Failed(string message) => new() { ErrorMessage = message };

    private async Task SafeNotify(Func<Task> send)
    {
        if (notifications is null) return;
        try { await send(); }
        catch (Exception ex)
        {
            // The issue transition is already durable. Keep notification transport failures observable
            // without rolling the user's report or an administrator action back.
            logger?.LogWarning(ex, "Could not deliver media issue lifecycle notification");
        }
    }

    private static MediaIssueDto ToDto(MediaIssueEntity issue, FulfillmentJobEntity? job,
        ImportedFileEntity? file, bool includeOperationalDetails = true) => new()
    {
        Id = issue.Id,
        MediaId = issue.MediaId,
        MediaType = issue.MediaType,
        Title = issue.Title,
        PosterUrl = issue.PosterUrl,
        ReportedByUserId = issue.ReportedByUserId,
        ReportedBy = issue.ReportedBy,
        Reason = issue.Reason,
        Detail = issue.Detail,
        SeasonNumber = issue.SeasonNumber,
        EpisodeNumber = issue.EpisodeNumber,
        EpisodeTitle = issue.EpisodeTitle,
        ImportedFileId = includeOperationalDetails ? issue.ImportedFileId : null,
        AffectedFilePath = file?.DestinationPath,
        AffectedReleaseName = file?.ReleaseName,
        AffectedResolution = file?.ResolutionHeight ?? 0,
        ReplacementRequested = issue.ReplacementRequested,
        ReplacementJobId = includeOperationalDetails ? issue.ReplacementJobId : null,
        ReplacementJobStatus = job?.Status,
        ReplacementProgress = job?.Progress ?? 0,
        ReplacementAttempts = includeOperationalDetails ? job?.Attempts ?? 0 : 0,
        ReplacementNextRetryAt = includeOperationalDetails ? job?.NextRetryAt : null,
        ReplacementLastError = includeOperationalDetails ? job?.LastError : null,
        Status = issue.Status,
        CreatedAt = issue.CreatedAt,
        ResolvedAt = issue.ResolvedAt
    };

    private static MediaRequestDto ToRequestDto(MediaRequestEntity request) => new()
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
        RequestAllSeasons = request.RequestAllSeasons,
        RequestedEpisodesCsv = request.RequestedEpisodesCsv,
        RequestScopeKind = request.RequestScopeKind,
        QualityProfileId = request.QualityProfileId,
        MediaRef = !string.IsNullOrWhiteSpace(request.ExternalId)
            ? MediaRef.FromExternal(request.ExternalSource ?? "external", request.ExternalId, request.MediaType,
                request.RequestScopeKind.ToMediaKind(request.MediaType))
            : MediaRef.FromTmdb(request.MediaId, request.MediaType),
        ExternalId = request.ExternalId,
        ExternalSource = request.ExternalSource,
        RequestedSeasons = string.IsNullOrWhiteSpace(request.RequestedSeasonsCsv) ? new() :
            request.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(x => int.TryParse(x, out var n) ? n : -1).Where(n => n >= 0).ToList()
    };
}
