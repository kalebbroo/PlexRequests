using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

public interface IStorageOptimizationService
{
    Task<List<StorageOptimizationTitleDto>> GetTitlesAsync();
    Task<StorageOptimizationPreviewDto> PreviewAsync(StorageOptimizationRequestDto request);
    Task<StorageOptimizationQueueResultDto> QueueAsync(StorageOptimizationRequestDto request);
    Task<List<StorageOptimizationActivityDto>> GetActivityAsync(int take = 50);
    Task<StorageOptimizationActionResultDto> CancelAsync(int jobId);
    Task<StorageOptimizationActionResultDto> RetryAsync(int jobId);
    Task<bool> RecordFailureAsync(int requestId, string reason);
}

/// <summary>
/// Builds an administrator-selected replacement scope from the import audit. Nothing here scans the entire
/// library or starts a download implicitly: preview and queue resolve the same deterministic target list,
/// and that list is frozen into the fulfillment job for every later safety check.
/// </summary>
public sealed class StorageOptimizationService(
    AppDbContext db,
    IFulfillmentQueue queue,
    ICustomFormatService customFormats,
    IReleaseParser parser) : IStorageOptimizationService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static readonly FulfillmentStatus[] ActiveStatuses =
        [FulfillmentStatus.Queued, FulfillmentStatus.Claimed, FulfillmentStatus.Downloading, FulfillmentStatus.Deferred];

    public async Task<List<StorageOptimizationTitleDto>> GetTitlesAsync()
    {
        var inventory = await LoadInventoryAsync();
        return inventory.GroupBy(row => row.Request.Id).Select(group =>
        {
            var request = group.First().Request;
            var files = group.Select(item => item.File).ToList();
            var descriptors = group.Select(Describe).ToList();
            return new StorageOptimizationTitleDto
            {
                RequestId = request.Id,
                Title = request.Title,
                Year = group.Select(item => item.Job.Year).FirstOrDefault(year => year.HasValue),
                MediaType = request.MediaType,
                IsAnime = request.IsAnime == true || request.MediaType == MediaType.Anime,
                PosterUrl = request.PosterUrl,
                FileCount = files.Count,
                SizeBytes = files.Sum(file => Math.Max(0, file.SizeBytes)),
                HevcFileCount = descriptors.Count(item => VideoCodecPolicy.Matches(item.Codec, "hevc")),
                UnknownCodecCount = descriptors.Count(item => item.Codec is null),
                HasActiveJob = group.Any(item => item.HasActiveJob),
                Seasons = request.MediaType is MediaType.TvShow or MediaType.Anime
                    ? descriptors.SelectMany(item => Coverage(item.Row.File).Select(ep => new { ep.Season, Item = item }))
                        .GroupBy(item => item.Season).OrderBy(item => item.Key)
                        .Select(season => new StorageOptimizationSeasonDto
                        {
                            Season = season.Key,
                            FileCount = season.Select(item => item.Item.Row.File.Id).Distinct().Count(),
                            SizeBytes = season.GroupBy(item => item.Item.Row.File.Id)
                                .Sum(item => Math.Max(0, item.First().Item.Row.File.SizeBytes)),
                            ResolutionSummary = Summary(season.Select(item => item.Item.Height > 0
                                ? QualityHelper.FromHeight(item.Item.Height).Label() : "Unknown")),
                            CodecSummary = Summary(season.Select(item => VideoCodecPolicy.Display(item.Item.Codec)))
                        }).ToList()
                    : new List<StorageOptimizationSeasonDto>()
            };
        }).OrderBy(item => item.Title, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public async Task<StorageOptimizationPreviewDto> PreviewAsync(StorageOptimizationRequestDto request) =>
        (await BuildAsync(request)).Preview;

    public async Task<StorageOptimizationQueueResultDto> QueueAsync(StorageOptimizationRequestDto request)
    {
        var built = await BuildAsync(request);
        if (!built.Preview.CanQueue || built.Request is null || built.Policy is null)
            return new StorageOptimizationQueueResultDto { Message = built.Preview.Message };

        var jobId = await queue.EnqueueOptimizationAsync(built.Request, built.Policy);
        return jobId is int id
            ? new StorageOptimizationQueueResultDto
            {
                Success = true,
                JobId = id,
                Message = $"Queued {built.Preview.SelectedFileCount} file(s) for verified optimization."
            }
            : new StorageOptimizationQueueResultDto
            {
                Message = "This title already has active download or replacement work. Try again after it finishes."
            };
    }

    public async Task<List<StorageOptimizationActivityDto>> GetActivityAsync(int take = 50)
    {
        var jobs = await db.FulfillmentJobs.AsNoTracking().Include(job => job.MediaRequest)
            .Where(job => job.StorageOptimizationPolicyJson != null)
            .OrderByDescending(job => job.Id)
            .Take(Math.Clamp(take, 1, 200))
            .ToListAsync();
        var formatNames = (await customFormats.GetAllAsync()).ToDictionary(format => format.Id, format => format.Name);
        return jobs.Select(job =>
        {
            var policy = ParsePolicy(job.StorageOptimizationPolicyJson);
            return new StorageOptimizationActivityDto
            {
                JobId = job.Id,
                RequestId = job.MediaRequestId,
                Title = job.Title,
                Year = job.Year,
                MediaType = job.MediaType,
                PosterUrl = job.MediaRequest?.PosterUrl,
                Status = job.Status,
                Stage = ActivityStage(job.Status, job.Progress),
                Progress = job.Progress,
                Attempts = job.Attempts,
                DeferCount = job.DeferCount,
                TargetFileCount = policy?.Targets.Count ?? 0,
                Seasons = policy?.Targets.SelectMany(target => target.EpisodeCoverage)
                    .Select(episode => episode.Season).Distinct().Order().ToList() ?? [],
                Goals = policy is null ? [] : DescribeGoals(policy, formatNames),
                OriginalBytes = policy?.CurrentBytes ?? 0,
                ReplacementBytes = job.StorageOptimizationReplacementBytes,
                LastError = job.LastError,
                CreatedAt = job.CreatedAt,
                UpdatedAt = job.CompletedAt ?? job.LastUpdatedAt ?? job.CreatedAt,
                NextRetryAt = job.NextRetryAt,
                CanCancel = job.Status is FulfillmentStatus.Queued or FulfillmentStatus.Deferred,
                CanRetry = job.Status is FulfillmentStatus.Deferred or FulfillmentStatus.Failed
                    or FulfillmentStatus.Cancelled
            };
        }).ToList();
    }

    public async Task<StorageOptimizationActionResultDto> CancelAsync(int jobId)
    {
        var now = DateTime.UtcNow;
        var changed = await db.FulfillmentJobs
            .Where(job => job.Id == jobId && job.StorageOptimizationPolicyJson != null
                          && (job.Status == FulfillmentStatus.Queued || job.Status == FulfillmentStatus.Deferred))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(job => job.Status, FulfillmentStatus.Cancelled)
                .SetProperty(job => job.LastError, "Cancelled by admin; original media was retained")
                .SetProperty(job => job.CompletedAt, now)
                .SetProperty(job => job.LastUpdatedAt, now)
                .SetProperty(job => job.NextRetryAt, (DateTime?)null)
                .SetProperty(job => job.ClaimedBy, (string?)null)
                .SetProperty(job => job.Progress, 0));
        return changed == 1
            ? new StorageOptimizationActionResultDto
            {
                Success = true,
                Message = "Optimization cancelled. Existing library files were left in place."
            }
            : new StorageOptimizationActionResultDto
            {
                Message = "This optimization is already transferring or has finished, so it was not cancelled."
            };
    }

    public async Task<StorageOptimizationActionResultDto> RetryAsync(int jobId)
    {
        var job = await db.FulfillmentJobs.Include(item => item.MediaRequest)
            .FirstOrDefaultAsync(item => item.Id == jobId && item.StorageOptimizationPolicyJson != null);
        if (job is null || job.Status is not (FulfillmentStatus.Deferred or FulfillmentStatus.Failed
                or FulfillmentStatus.Cancelled))
            return new StorageOptimizationActionResultDto { Message = "This optimization is not retryable." };

        var anotherActive = await db.FulfillmentJobs.AnyAsync(item => item.Id != job.Id
            && item.MediaRequestId == job.MediaRequestId && ActiveStatuses.Contains(item.Status));
        if (anotherActive)
            return new StorageOptimizationActionResultDto
            {
                Message = "Another download or replacement already owns this title. Retry after it finishes."
            };

        job.Status = FulfillmentStatus.Queued;
        job.Progress = 0;
        job.LastError = null;
        job.CompletedAt = null;
        job.NextRetryAt = null;
        job.ClaimedBy = null;
        job.ClaimedAt = null;
        job.LastUpdatedAt = DateTime.UtcNow;
        // An optimization starts from an available title. Repair the old generic failure behavior if this
        // row was ever sent through that endpoint, without changing first-time fulfillment requests.
        if (job.MediaRequest is { } request && request.Status == RequestStatus.Failed)
        {
            request.Status = RequestStatus.Available;
            request.DenialReason = null;
            request.AvailableAt ??= DateTime.UtcNow;
        }
        try
        {
            await db.SaveChangesAsync();
            return new StorageOptimizationActionResultDto
            {
                Success = true,
                Message = "Optimization queued for another search."
            };
        }
        catch (DbUpdateException)
        {
            db.ChangeTracker.Clear();
            return new StorageOptimizationActionResultDto
            {
                Message = "Another download or replacement claimed this title first."
            };
        }
    }

    /// <summary>Keep generic worker failures from changing an already-available request. This compatibility
    /// boundary matters while workers report failures by request id rather than by optimization job id.</summary>
    public async Task<bool> RecordFailureAsync(int requestId, string reason)
    {
        var job = await db.FulfillmentJobs.Include(item => item.MediaRequest)
            .Where(item => item.MediaRequestId == requestId && item.StorageOptimizationPolicyJson != null
                           && ActiveStatuses.Contains(item.Status))
            .OrderByDescending(item => item.Id)
            .FirstOrDefaultAsync();
        if (job is null) return false;

        var now = DateTime.UtcNow;
        job.Status = FulfillmentStatus.Failed;
        job.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        job.CompletedAt = now;
        job.LastUpdatedAt = now;
        job.ClaimedBy = null;
        job.NextRetryAt = null;
        if (job.MediaRequest is { } request)
        {
            request.Status = RequestStatus.Available;
            request.DenialReason = null;
            request.AvailableAt ??= now;
        }
        await db.SaveChangesAsync();
        return true;
    }

    private async Task<BuiltOptimization> BuildAsync(StorageOptimizationRequestDto input)
    {
        var preview = new StorageOptimizationPreviewDto { RequestId = input.RequestId };
        if (input.RequestId <= 0)
            return new(preview.WithMessage("Choose a title first."), null, null);
        if (!Enum.IsDefined(input.TargetQuality) || input.TargetQuality == Quality.UHD8K)
            return new(preview.WithMessage("Choose a supported resolution target."), null, null);

        var requiredIds = input.RequiredCustomFormatIds.Where(id => id > 0).Distinct().ToList();
        if (!input.RequireHevc && input.TargetQuality == Quality.Any && requiredIds.Count == 0)
            return new(preview.WithMessage("Choose at least one goal: HEVC, a resolution, or a reusable format."), null, null);

        var rows = (await LoadInventoryAsync()).Where(item => item.Request.Id == input.RequestId).ToList();
        if (rows.Count == 0)
            return new(preview.WithMessage("No current imported video files were found for this title."), null, null);
        var request = rows[0].Request;
        preview.Title = request.Title;
        if (rows.Any(item => item.HasActiveJob))
            return new(preview.WithMessage("This title already has active download or replacement work."), null, null);

        var selectedSeasons = input.Seasons.Where(season => season >= 0).Distinct().Order().ToList();
        if (request.MediaType is MediaType.TvShow or MediaType.Anime)
        {
            if (selectedSeasons.Count == 0)
                selectedSeasons = rows.SelectMany(item => Coverage(item.File)).Select(item => item.Season)
                    .Distinct().Order().ToList();
            rows = rows.Where(item => Coverage(item.File).Any(ep => selectedSeasons.Contains(ep.Season))).ToList();
        }
        else selectedSeasons.Clear();
        if (rows.Count == 0)
            return new(preview.WithMessage("The selected seasons have no imported video files."), null, null);

        var formats = await customFormats.GetAllAsync();
        var knownIds = formats.Where(format => format.Enabled).Select(format => format.Id).ToHashSet();
        if (requiredIds.Any(id => !knownIds.Contains(id)))
            return new(preview.WithMessage("One of the selected reusable formats no longer exists or is disabled."), null, null);

        var targets = new List<StorageOptimizationTargetDto>();
        var unknown = 0;
        var downscaleCount = 0;
        foreach (var row in rows)
        {
            var descriptor = Describe(row);
            var tier = QualityHelper.FromHeight(descriptor.Height);
            var codecMissing = input.RequireHevc && !VideoCodecPolicy.Matches(descriptor.Codec, "hevc");
            var resolutionMissing = input.TargetQuality != Quality.Any && tier != input.TargetQuality;
            var currentMatches = MatchedFormats(row.File, formats);
            var formatMissing = requiredIds.Any(id => !currentMatches.Contains(id));
            if (!codecMissing && !resolutionMissing && !formatMissing) continue;
            if (descriptor.Codec is null) unknown++;
            if (input.TargetQuality != Quality.Any && tier > input.TargetQuality) downscaleCount++;
            targets.Add(new StorageOptimizationTargetDto
            {
                DestinationPath = row.File.DestinationPath,
                EpisodeCoverage = Coverage(row.File),
                CurrentSizeBytes = row.File.SizeBytes,
                CurrentResolutionHeight = descriptor.Height,
                CurrentVideoCodec = descriptor.Codec
            });
        }

        preview.SelectedFileCount = targets.Count;
        preview.UnknownCodecCount = unknown;
        preview.CurrentBytes = targets.Sum(target => Math.Max(0, target.CurrentSizeBytes));
        preview.MaximumReplacementBytes = input.MinimumSavingsPercent <= 0 ? long.MaxValue
            : (long)Math.Floor(preview.CurrentBytes *
                (100d - Math.Clamp(input.MinimumSavingsPercent, 0, 95)) / 100d);
        preview.EffectiveTargetQuality = input.TargetQuality != Quality.Any
            ? input.TargetQuality
            : targets.Select(target => QualityHelper.FromHeight(target.CurrentResolutionHeight))
                .DefaultIfEmpty(Quality.Any).Max();
        preview.Seasons = selectedSeasons;
        if (input.RequireHevc) preview.Goals.Add("HEVC (H.265)");
        if (input.TargetQuality != Quality.Any) preview.Goals.Add(input.TargetQuality.Label());
        preview.Goals.AddRange(formats.Where(format => requiredIds.Contains(format.Id)).Select(format => format.Name));
        if (input.MinimumSavingsPercent > 0) preview.Goals.Add($"save at least {input.MinimumSavingsPercent}%");

        if (targets.Count == 0)
            return new(preview.WithMessage("Everything in this scope already satisfies the selected goals."), null, null);
        if (input.MinimumSavingsPercent > 0 && targets.Any(target => target.CurrentSizeBytes <= 0))
            return new(preview.WithMessage("Some current file sizes are unknown, so a minimum saving cannot be proven. Set minimum savings to 0 or choose another scope."), null, null);

        var policy = new StorageOptimizationPolicyDto
        {
            TargetQuality = input.TargetQuality,
            RequiredVideoCodec = input.RequireHevc ? "hevc" : null,
            RequiredCustomFormatIds = requiredIds,
            MinimumSavingsPercent = Math.Clamp(input.MinimumSavingsPercent, 0, 95),
            Targets = targets
        };
        preview.CanQueue = true;
        preview.Message = $"{targets.Count} file(s) will be replaced only after every selected goal is verified."
            + (unknown > 0
                ? $" {unknown} current codec value(s) are unknown; replacement codecs will still be verified."
                : string.Empty)
            + (downscaleCount > 0
                ? $" Warning: the exact resolution choice will reduce {downscaleCount} higher-resolution file(s)."
                : string.Empty);
        return new(preview, ToRequest(request), policy);
    }

    private async Task<List<InventoryRow>> LoadInventoryAsync()
    {
        var activeRequestIds = await db.FulfillmentJobs.AsNoTracking()
            .Where(job => ActiveStatuses.Contains(job.Status)).Select(job => job.MediaRequestId).Distinct().ToListAsync();
        var files = await db.ImportedFiles.AsNoTracking().Include(item => item.EpisodeCoverage)
            .Where(file => file.FileType == "video").ToListAsync();
        var jobIds = files.Select(file => file.FulfillmentJobId).Distinct().ToList();
        var jobs = await db.FulfillmentJobs.AsNoTracking().Where(job => jobIds.Contains(job.Id))
            .ToDictionaryAsync(job => job.Id);
        var requestIds = jobs.Values.Select(job => job.MediaRequestId).Distinct().ToList();
        var requests = await db.MediaRequests.AsNoTracking()
            .Where(request => requestIds.Contains(request.Id)
                              && request.Status == RequestStatus.Available
                              && request.MediaType != MediaType.Music)
            .ToDictionaryAsync(request => request.Id);
        var rows = files.Where(file => jobs.ContainsKey(file.FulfillmentJobId))
            .Select(file => (file, job: jobs[file.FulfillmentJobId]))
            .Where(item => requests.ContainsKey(item.job.MediaRequestId))
            .Select(item => new InventoryRow(item.file, item.job, requests[item.job.MediaRequestId],
                activeRequestIds.Contains(item.job.MediaRequestId))).ToList();

        // Legacy retries could record the same destination more than once. The newest audit row represents
        // the physical file; counting both would inflate savings and could queue duplicate path replacement.
        return rows.GroupBy(row => row.File.DestinationPath, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.OrderByDescending(row => row.File.ImportedAt).ThenByDescending(row => row.File.Id).First())
            .ToList();
    }

    private FileDescriptor Describe(InventoryRow row)
    {
        MediaTrackSummaryDto? tracks = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(row.File.MediaTracksJson))
                tracks = JsonSerializer.Deserialize<MediaTrackSummaryDto>(row.File.MediaTracksJson, Json);
        }
        catch (JsonException) { }
        var video = tracks?.Video.FirstOrDefault();
        var parsed = string.IsNullOrWhiteSpace(row.File.ReleaseName) ? null : parser.Parse(row.File.ReleaseName);
        var observedQuality = VideoResolutionPolicy.FromDimensions(video?.Width, video?.Height);
        return new(row, video?.Codec ?? parsed?.Codec,
            observedQuality != Quality.Any ? (int)observedQuality : row.File.ResolutionHeight);
    }

    private HashSet<int> MatchedFormats(ImportedFileEntity file, IReadOnlyList<CustomFormatDto> formats)
    {
        if (string.IsNullOrWhiteSpace(file.ReleaseName)) return new HashSet<int>();
        var parsed = parser.Parse(file.ReleaseName);
        var candidate = new ReleaseCandidate
        {
            ReleaseName = file.ReleaseName,
            SizeBytes = Math.Max(0, file.SizeBytes),
            Acquisition = AcquisitionResource.Torrent("audit", file.InfoHash),
            Source = "import-audit"
        };
        return formats.Where(format => format.Enabled && CustomFormatMatcher.Matches(format, parsed, candidate))
            .Select(format => format.Id).ToHashSet();
    }

    private static List<EpisodeRef> Coverage(ImportedFileEntity file) => file.EpisodeCoverage.Count > 0
        ? file.EpisodeCoverage.Select(item => new EpisodeRef
        { Season = item.SeasonNumber, Episode = item.EpisodeNumber }).ToList()
        : file.SeasonNumber is int season && file.EpisodeNumber is int episode
            ? [new EpisodeRef { Season = season, Episode = episode }]
            : [];

    private static string Summary(IEnumerable<string> values)
    {
        var counts = values.GroupBy(value => value).OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.OrdinalIgnoreCase).ToList();
        return string.Join(" · ", counts.Select(group => counts.Count == 1 ? group.Key : $"{group.Key} ×{group.Count()}"));
    }

    private static StorageOptimizationPolicyDto? ParsePolicy(string? json)
    {
        try { return string.IsNullOrWhiteSpace(json) ? null : JsonSerializer.Deserialize<StorageOptimizationPolicyDto>(json, Json); }
        catch (JsonException) { return null; }
    }

    private static List<string> DescribeGoals(StorageOptimizationPolicyDto policy,
        IReadOnlyDictionary<int, string> formatNames)
    {
        var goals = new List<string>();
        if (!string.IsNullOrWhiteSpace(policy.RequiredVideoCodec))
            goals.Add(VideoCodecPolicy.Display(policy.RequiredVideoCodec));
        if (policy.TargetQuality != Quality.Any) goals.Add(policy.TargetQuality.Label());
        goals.AddRange(policy.RequiredCustomFormatIds.Distinct()
            .Select(id => formatNames.TryGetValue(id, out var name) ? name : $"Format #{id}"));
        if (policy.MinimumSavingsPercent > 0) goals.Add($"Save at least {policy.MinimumSavingsPercent}%");
        return goals;
    }

    private static string ActivityStage(FulfillmentStatus status, int progress) => status switch
    {
        FulfillmentStatus.Queued => "Queued to optimize",
        FulfillmentStatus.Claimed => "Searching for a replacement",
        FulfillmentStatus.Downloading when progress >= 100 => "Verifying and replacing",
        FulfillmentStatus.Downloading => "Downloading replacement",
        FulfillmentStatus.Deferred => "Waiting for a matching release",
        FulfillmentStatus.Completed => "Optimization completed",
        FulfillmentStatus.PartiallyCompleted => "Waiting for remaining files",
        FulfillmentStatus.Failed => "Needs attention",
        FulfillmentStatus.Cancelled => "Cancelled",
        _ => status.ToString()
    };

    private static MediaRequestDto ToRequest(MediaRequestEntity request) => new()
    {
        Id = request.Id,
        MediaId = request.MediaId,
        MediaType = request.MediaType,
        MediaRef = !string.IsNullOrWhiteSpace(request.ExternalId)
            ? PlexRequestsHosted.Shared.Media.MediaRef.FromExternal(request.ExternalSource ?? "external",
                request.ExternalId, request.MediaType, request.RequestScopeKind.ToMediaKind(request.MediaType))
            : PlexRequestsHosted.Shared.Media.MediaRef.FromTmdb(request.MediaId, request.MediaType),
        RequestScopeKind = request.RequestScopeKind,
        ExternalId = request.ExternalId,
        ExternalSource = request.ExternalSource,
        Title = request.Title,
        PosterUrl = request.PosterUrl,
        Status = request.Status,
        QualityProfileId = request.QualityProfileId,
        LibraryDestinationId = request.LibraryDestinationId,
        IsAnime = request.IsAnime
    };

    private sealed record InventoryRow(ImportedFileEntity File, FulfillmentJobEntity Job,
        MediaRequestEntity Request, bool HasActiveJob);
    private sealed record FileDescriptor(InventoryRow Row, string? Codec, int Height);
    private sealed record BuiltOptimization(StorageOptimizationPreviewDto Preview, MediaRequestDto? Request,
        StorageOptimizationPolicyDto? Policy);
}

file static class StorageOptimizationPreviewExtensions
{
    public static StorageOptimizationPreviewDto WithMessage(this StorageOptimizationPreviewDto preview, string message)
    {
        preview.Message = message;
        return preview;
    }
}
