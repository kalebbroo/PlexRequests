using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Media;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

public interface IInteractiveSearchService
{
    /// <summary>Queue a search. Returns the task id to poll.</summary>
    Task<int> CreateAsync(int? mediaRequestId, MediaType mediaType, int mediaId, string title,
        int? year, string? imdbId, int? season, int? episode, int? userId);
    Task<int> CreateAsync(int? mediaRequestId, MediaRef mediaRef, RequestScopeKind scope, string title,
        int? year, string? imdbId, MusicAcquisitionContextDto? music, int? userId);

    /// <summary>Queue a capabilities probe against one indexer.</summary>
    Task<int> CreateCapabilitiesTestAsync(int indexerId, int? userId);

    Task<SearchTaskStatusDto?> GetAsync(int id);

    /// <summary>Force-grab a specific release, bypassing search and ranking entirely.</summary>
    Task<(bool ok, string? error)> GrabAsync(int taskId, string magnet, string releaseName, int indexerId, int? userId);

    // ---- Worker-facing ----
    Task<SearchTaskDto?> ClaimAsync(string workerId);
    Task<bool> CompleteAsync(int taskId, SearchTaskResultDto result);
}

/// <summary>
/// Runs admin-initiated searches by handing work to the downloader and collecting what it finds.
///
/// The indirection is not incidental: the web app has no route to the downloader — it's a worker with no
/// listening port, sharing the VPN container's network namespace — so anything the web app wants done has
/// to be something the worker pulls. The worker polls on a short interval, which puts the admin-perceived
/// latency at a couple of seconds plus the search itself.
/// </summary>
public class InteractiveSearchService(
    AppDbContext db,
    IQualityProfileService profiles,
    ILogger<InteractiveSearchService> logger) : IInteractiveSearchService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    /// <summary>Results are capped before storing. This table is transient scratch space, not a data store.</summary>
    private const int MaxStoredResults = 300;
    private static readonly TimeSpan Lifetime = TimeSpan.FromMinutes(30);

    public async Task<int> CreateAsync(int? mediaRequestId, MediaType mediaType, int mediaId, string title,
        int? year, string? imdbId, int? season, int? episode, int? userId)
    {
        var scope = episode.HasValue ? RequestScopeKind.Episodes
            : season.HasValue ? RequestScopeKind.Seasons
            : mediaType is MediaType.TvShow or MediaType.Anime ? RequestScopeKind.Series
            : RequestScopeKind.Title;
        return await CreateCoreAsync(mediaRequestId, MediaRef.FromTmdb(mediaId, mediaType), scope, title,
            year, imdbId, season, episode, null, userId);
    }

    public Task<int> CreateAsync(int? mediaRequestId, MediaRef mediaRef, RequestScopeKind scope, string title,
        int? year, string? imdbId, MusicAcquisitionContextDto? music, int? userId) =>
        CreateCoreAsync(mediaRequestId, mediaRef, scope, title, year, imdbId, null, null, music, userId);

    private async Task<int> CreateCoreAsync(int? mediaRequestId, MediaRef mediaRef, RequestScopeKind scope,
        string title, int? year, string? imdbId, int? season, int? episode,
        MusicAcquisitionContextDto? music, int? userId)
    {
        int? profileId = mediaRequestId is int rid
            ? await db.MediaRequests.Where(r => r.Id == rid).Select(r => r.QualityProfileId).FirstOrDefaultAsync()
            : null;
        profileId ??= await db.QualityProfiles.Where(p => p.IsDefault).Select(p => (int?)p.Id).FirstOrDefaultAsync();

        var task = new SearchTaskEntity
        {
            Kind = SearchTaskKind.InteractiveSearch,
            MediaRequestId = mediaRequestId,
            MediaType = mediaRef.MediaType,
            MediaId = mediaRef.TryGetTmdbId(out var tmdbId) ? tmdbId : 0,
            MediaKind = mediaRef.Kind,
            RequestScopeKind = scope,
            ExternalId = mediaRef.Provider == "tmdb" ? null : mediaRef.Id,
            ExternalSource = mediaRef.Provider == "tmdb" ? null : mediaRef.Provider,
            AcquisitionContextJson = music is null ? null : JsonSerializer.Serialize(music, Json),
            Title = title,
            Year = year,
            ImdbId = imdbId,
            Season = season,
            Episode = episode,
            QualityProfileId = profileId,
            RequestedByUserId = userId,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        };
        db.SearchTasks.Add(task);
        await db.SaveChangesAsync();
        logger.LogInformation("Interactive search #{Id} queued for \"{Title}\"", task.Id, title);
        return task.Id;
    }

    public async Task<int> CreateCapabilitiesTestAsync(int indexerId, int? userId)
    {
        var task = new SearchTaskEntity
        {
            Kind = SearchTaskKind.CapabilitiesTest,
            IndexerId = indexerId,
            Title = "capabilities",
            RequestedByUserId = userId,
            ExpiresAt = DateTime.UtcNow.Add(Lifetime)
        };
        db.SearchTasks.Add(task);
        await db.SaveChangesAsync();
        return task.Id;
    }

    public async Task<SearchTaskStatusDto?> GetAsync(int id)
    {
        var t = await db.SearchTasks.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        if (t is null) return null;
        return new SearchTaskStatusDto
        {
            Id = t.Id,
            Status = t.Status,
            Error = t.Error,
            IndexerSummary = t.IndexerOutcomesJson,
            Results = string.IsNullOrWhiteSpace(t.ResultsJson)
                ? new()
                : JsonSerializer.Deserialize<List<SearchResultDto>>(t.ResultsJson, Json) ?? new(),
            Capabilities = string.IsNullOrWhiteSpace(t.CapabilitiesJson)
                ? null
                : JsonSerializer.Deserialize<IndexerCapabilitiesDto>(t.CapabilitiesJson, Json)
        };
    }

    public async Task<(bool ok, string? error)> GrabAsync(int taskId, string magnet, string releaseName, int indexerId, int? userId)
    {
        var task = await db.SearchTasks.AsNoTracking().FirstOrDefaultAsync(t => t.Id == taskId);
        if (task is null) return (false, "That search has expired — run it again.");
        if (string.IsNullOrWhiteSpace(magnet)) return (false, "That release has no magnet link.");

        var request = task.MediaRequestId is int rid
            ? await db.MediaRequests.FirstOrDefaultAsync(r => r.Id == rid)
            : null;
        if (request is null) return (false, "A forced grab needs an existing request to attach to.");

        // Close any job already working this request; the admin's explicit choice supersedes it.
        var existing = await db.FulfillmentJobs
            .Where(j => j.MediaRequestId == request.Id
                        && (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed
                            || j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred))
            .ToListAsync();
        foreach (var j in existing)
        {
            j.Status = FulfillmentStatus.Cancelled;
            j.LastError = "Superseded by a manually grabbed release";
            j.CompletedAt = DateTime.UtcNow;
        }

        db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaIdentityId = request.MediaIdentityId,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            MediaKind = task.MediaKind,
            RequestScopeKind = request.RequestScopeKind,
            ExternalId = request.ExternalId,
            ExternalSource = request.ExternalSource,
            AcquisitionContextJson = task.AcquisitionContextJson,
            Title = request.Title,
            TmdbId = string.IsNullOrWhiteSpace(request.ExternalId) ? request.MediaId : null,
            Quality = task.QualityProfileId is int pid ? await profiles.GetCutoffQualityAsync(pid) : Quality.Any,
            QualityProfileId = task.QualityProfileId,
            RequestedSeasonsCsv = task.Season?.ToString(),
            RequestedEpisodesCsv = task is { Season: int s, Episode: int e } ? $"S{s}E{e}" : null,
            Status = FulfillmentStatus.Queued,
            // These make the downloader skip searching and ranking entirely.
            IsManualGrab = true,
            ForcedMagnet = magnet,
            ForcedReleaseName = releaseName,
            ForcedIndexerId = indexerId,
            CreatedAt = DateTime.UtcNow
        });

        request.Status = RequestStatus.Approved;
        await db.SaveChangesAsync();
        logger.LogInformation("Manual grab queued for request #{RequestId}: \"{Release}\"", request.Id, releaseName);
        return (true, null);
    }

    // ---- Worker-facing ------------------------------------------------------------------------------

    public async Task<SearchTaskDto?> ClaimAsync(string workerId)
    {
        var now = DateTime.UtcNow;
        var task = await db.SearchTasks
            .Where(t => t.Status == SearchTaskStatus.Pending && t.ExpiresAt > now)
            .OrderBy(t => t.CreatedAt)
            .FirstOrDefaultAsync();
        if (task is null) return null;

        task.Status = SearchTaskStatus.Claimed;
        task.ClaimedBy = workerId;
        task.ClaimedAt = now;
        await db.SaveChangesAsync();

        var dto = new SearchTaskDto
        {
            Id = task.Id,
            Kind = task.Kind,
            MediaRequestId = task.MediaRequestId,
            MediaType = task.MediaType,
            MediaId = task.MediaId,
            Media = !string.IsNullOrWhiteSpace(task.ExternalId)
                ? MediaRef.FromExternal(task.ExternalSource ?? "external", task.ExternalId,
                    task.MediaType, task.MediaKind)
                : MediaRef.FromTmdb(task.MediaId, task.MediaType),
            RequestScope = task.RequestScopeKind,
            Music = string.IsNullOrWhiteSpace(task.AcquisitionContextJson)
                ? null
                : JsonSerializer.Deserialize<MusicAcquisitionContextDto>(task.AcquisitionContextJson, Json),
            Title = task.Title,
            Year = task.Year,
            ImdbId = task.ImdbId,
            Season = task.Season,
            Episode = task.Episode,
            IndexerId = task.IndexerId,
            IncludeRejected = task.IncludeRejected
        };

        if (task.MediaType == MediaType.Music && await db.MusicSettings.AsNoTracking()
                .Where(x => x.IsSingleton).Select(x => x.DirectDownloadsEnabled).FirstOrDefaultAsync())
            dto.AllowedAcquisitionProtocols.Add(AcquisitionProtocol.DirectAudio);

        if (task.QualityProfileId is int pid) dto.QualityProfile = await profiles.GetProfileAsync(pid);
        dto.QualityDefinitions = await db.QualityDefinitions.AsNoTracking().OrderBy(d => d.SortWeight)
            .Select(d => new QualityDefinitionDto
            {
                Id = d.Id, Name = d.Name, Resolution = d.Resolution, Source = d.Source, SortWeight = d.SortWeight
            }).ToListAsync();

        if (task.MediaRequestId is int reqId)
        {
            var blocked = await db.ReleaseBlocklist
                .Where(b => (b.SourceId != null || b.InfoHash != null)
                    && (b.MediaRequestId == reqId || b.Scope != BlocklistScope.Request))
                .Select(b => new { b.Protocol, b.SourceId, b.InfoHash }).ToListAsync();
            dto.BlocklistedHashes = blocked.SelectMany(b => new[]
                {
                    b.InfoHash,
                    b.SourceId is null ? null : AcquisitionResource.BlocklistKey(b.Protocol, b.SourceId)
                }).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        return dto;
    }

    public async Task<bool> CompleteAsync(int taskId, SearchTaskResultDto result)
    {
        var task = await db.SearchTasks.FirstOrDefaultAsync(t => t.Id == taskId);
        if (task is null) return false;

        // Keep the best accepted results and a bounded slice of the rejected ones — the rejected rows are
        // the point of an interactive search, but not at unbounded cost.
        var capped = result.Results
            .OrderByDescending(r => r.Accepted).ThenByDescending(r => r.Score)
            .Take(MaxStoredResults).ToList();

        task.Status = string.IsNullOrWhiteSpace(result.Error) ? SearchTaskStatus.Completed : SearchTaskStatus.Failed;
        task.ResultsJson = JsonSerializer.Serialize(capped, Json);
        task.IndexerOutcomesJson = result.IndexerSummary;
        task.Error = result.Error is { Length: > 2000 } e ? e[..2000] : result.Error;
        task.CompletedAt = DateTime.UtcNow;

        // A capabilities test reports a reachability failure as an Error so the task reads as Failed, but
        // the probe detail is still worth keeping — that's what tells the admin whether it was DNS, auth or
        // a wrong URL.
        if (result.Capabilities is not null)
            task.CapabilitiesJson = JsonSerializer.Serialize(result.Capabilities, Json);

        await db.SaveChangesAsync();

        if (task.Kind == SearchTaskKind.CapabilitiesTest)
            logger.LogInformation("Capabilities test #{Id} completed: {Message}", taskId, result.Capabilities?.Message);
        else
            logger.LogInformation("Interactive search #{Id} completed: {Accepted} acceptable of {Total} candidate(s)",
                taskId, capped.Count(r => r.Accepted), capped.Count);
        return true;
    }
}
