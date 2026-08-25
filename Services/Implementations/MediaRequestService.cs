using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.Media;

namespace PlexRequestsHosted.Services.Implementations;

public class MediaRequestService(
    AppDbContext db,
    AuthenticationStateProvider authStateProvider,
    IMediaMetadataProvider metadataProvider,
    INotificationService notificationService,
    IFulfillmentQueue fulfillmentQueue,
    IConfiguration configuration,
    IDownloadPreferencesService downloadPreferences,
    ISeasonAvailabilityEvaluator seasonAvailability,
    IQualityProfileService qualityProfiles,
    IMediaIdentityService mediaIdentities,
    IMediaModuleRegistry mediaModules,
    IMusicSettingsService musicSettings,
    IPlexMusicService plexMusic,
    IUserAccessService userAccess,
    IUserQuotaService userQuotas) : IMediaRequestService
{
    private readonly AppDbContext _db = db;
    private readonly AuthenticationStateProvider _auth = authStateProvider;
    private readonly IMediaMetadataProvider _metadata = metadataProvider;
    private readonly INotificationService _notify = notificationService;
    private readonly IFulfillmentQueue _fulfillment = fulfillmentQueue;
    private readonly IConfiguration _config = configuration;
    private readonly IDownloadPreferencesService _downloadPreferences = downloadPreferences;
    private readonly ISeasonAvailabilityEvaluator _seasonAvailability = seasonAvailability;
    private readonly IQualityProfileService _qualityProfiles = qualityProfiles;
    private readonly IMediaIdentityService _mediaIdentities = mediaIdentities;
    private readonly IMediaModuleRegistry _mediaModules = mediaModules;
    private readonly IMusicSettingsService _musicSettings = musicSettings;
    private readonly IPlexMusicService _plexMusic = plexMusic;
    private readonly IUserAccessService _userAccess = userAccess;
    private readonly IUserQuotaService _userQuotas = userQuotas;

    // Statuses that still legitimately block a duplicate request/re-request; Failed/Cancelled/Rejected
    // don't (a failed download should be retryable), and Available is checked separately/live so its
    // message can be specific rather than folded into "you've already requested this". Searching counts
    // as active: the downloader is still re-trying it on a backoff, so a re-request would only duplicate.
    private static bool IsActiveStatus(RequestStatus s) =>
        s is RequestStatus.Pending or RequestStatus.Approved or RequestStatus.Processing or RequestStatus.Searching;

    private async Task<RequestActor> GetActorAsync()
    {
        var principal = (await _auth.GetAuthenticationStateAsync()).User;
        if (principal.Identity?.IsAuthenticated != true)
            return new RequestActor(null, string.Empty, false);
        var username = principal.Identity?.Name ?? string.Empty;
        int? userId = int.TryParse(principal.FindFirst("user_id")?.Value, out var claimedId) && claimedId > 0
            ? claimedId
            : string.IsNullOrWhiteSpace(username)
                ? null
                : await _db.Users.Where(x => x.Username == username).Select(x => (int?)x.Id).FirstOrDefaultAsync();
        // Username fallback keeps old rows readable until their immutable owner id is backfilled.
        if (userId is null) return new RequestActor(null, username, false);
        var access = await _userAccess.GetAccessAsync(userId.Value);
        return access.IsActive
            ? new RequestActor(userId, username, access.IsAdmin)
            : new RequestActor(null, string.Empty, false);
    }

    private static IQueryable<MediaRequestEntity> OwnedRequests(
        IQueryable<MediaRequestEntity> query, RequestActor actor) => actor.UserId is int userId
        ? query.Where(x => x.RequestedByUserId == userId
                           || (x.RequestedByUserId == null && x.RequestedBy == actor.Username))
        : !string.IsNullOrWhiteSpace(actor.Username)
            ? query.Where(x => x.RequestedByUserId == null && x.RequestedBy == actor.Username)
            : query.Where(_ => false);

    private static IQueryable<WatchlistItemEntity> OwnedWatchlist(
        IQueryable<WatchlistItemEntity> query, RequestActor actor) => actor.UserId is int userId
        ? query.Where(x => x.UserId == userId || (x.UserId == null && x.Username == actor.Username))
        : !string.IsNullOrWhiteSpace(actor.Username)
            ? query.Where(x => x.UserId == null && x.Username == actor.Username)
            : query.Where(_ => false);

    public async Task<bool> AddToWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username)) return false;
        // Avoid duplicates for the same (user, media, type)
        var already = await OwnedWatchlist(_db.Watchlist, actor)
            .AnyAsync(w => w.MediaId == mediaId && w.MediaType == mediaType);
        if (already) return true;
        var identity = await _mediaIdentities.ResolveAsync(MediaRef.FromTmdb(mediaId, mediaType));
        _db.Watchlist.Add(new WatchlistItemEntity
        {
            MediaIdentityId = identity.Id,
            MediaId = mediaId,
            MediaType = mediaType,
            UserId = actor.UserId,
            Username = actor.Username
        });
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> CancelRequestAsync(int requestId)
    {
        var actor = await GetActorAsync();
        var req = await OwnedRequests(_db.MediaRequests, actor)
            .FirstOrDefaultAsync(r => r.Id == requestId && r.Status == RequestStatus.Pending);
        if (req is null) return false;
        req.Status = RequestStatus.Cancelled;
        var changed = await _db.SaveChangesAsync() > 0;
        if (changed) await _notify.RequestCancelledAsync(ToDto(req));
        return changed;
    }

    public async Task<UserStatsDto> GetMyStatsAsync()
    {
        var actor = await GetActorAsync();
        var q = OwnedRequests(_db.MediaRequests.AsNoTracking(), actor);
        var stats = new UserStatsDto
        {
            TotalRequests = await q.CountAsync(),
            ApprovedRequests = await q.CountAsync(r => r.Status == RequestStatus.Approved),
            PendingRequests = await q.CountAsync(r => r.Status == RequestStatus.Pending),
            AvailableRequests = await q.CountAsync(r => r.Status == RequestStatus.Available),
            LastRequestDate = await q.OrderByDescending(r => r.RequestedAt).Select(r => (DateTime?)r.RequestedAt).FirstOrDefaultAsync()
        };
        return stats;
    }

    public async Task<List<MediaCardDto>> GetWatchlistAsync()
    {
        var actor = await GetActorAsync();
        var items = await OwnedWatchlist(_db.Watchlist.AsNoTracking(), actor)
            .OrderByDescending(w => w.AddedAt)
            .Select(w => new { w.MediaId, w.MediaType })
            .ToListAsync();

        var cards = new List<MediaCardDto>(items.Count);
        foreach (var it in items)
        {
            MediaCardDto? card = null;
            try
            {
                var d = await _metadata.GetDetailsAsync(it.MediaId, it.MediaType);
                if (d is not null)
                {
                    card = new MediaCardDto
                    {
                        Id = d.Id,
                        Title = d.Title,
                        Overview = d.Overview,
                        PosterUrl = d.PosterUrl,
                        BackdropUrl = d.BackdropUrl,
                        Year = d.Year,
                        Rating = d.Rating,
                        MediaType = it.MediaType,
                        Genres = d.Genres,
                        TmdbId = d.TmdbId,
                        RequestStatus = RequestStatus.None
                    };
                }
            }
            catch { /* metadata provider unavailable; fall back to a minimal card */ }

            card ??= new MediaCardDto { Id = it.MediaId, Title = $"Item #{it.MediaId}", MediaType = it.MediaType, RequestStatus = RequestStatus.None };
            cards.Add(card);
        }
        return cards;
    }

    public async Task<MediaRequestDto?> GetRequestByIdAsync(int id)
    {
        var actor = await GetActorAsync();
        var query = _db.MediaRequests.AsNoTracking().AsQueryable();
        if (!actor.IsAdmin) query = OwnedRequests(query, actor);
        var r = await query.FirstOrDefaultAsync(r => r.Id == id);
        return r is null ? null : ToDto(r);
    }

    public async Task<PagedResult<MediaRequestDto>> GetRequestsAsync(MediaFilterDto filter)
    {
        var actor = await GetActorAsync();
        return await GetRequestsPageAsync(OwnedRequests(_db.MediaRequests.AsQueryable(), actor), filter);
    }

    public async Task<PagedResult<MediaRequestDto>> GetAdminRequestsAsync(MediaFilterDto filter)
    {
        var actor = await GetActorAsync();
        if (!actor.IsAdmin)
            return EmptyRequestPage(filter);
        return await GetRequestsPageAsync(_db.MediaRequests.AsQueryable(), filter);
    }

    private async Task<PagedResult<MediaRequestDto>> GetRequestsPageAsync(
        IQueryable<MediaRequestEntity> query,
        MediaFilterDto filter)
    {
        var pageNumber = Math.Max(1, filter.PageNumber);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        if (filter.RequestStatus is RequestStatus status)
            query = query.Where(r => r.Status == status);
        if (filter.MediaType is MediaType mediaType)
            query = query.Where(r => r.MediaType == mediaType);
        if (!string.IsNullOrWhiteSpace(filter.SearchTerm))
        {
            var term = filter.SearchTerm.Trim();
            query = query.Where(r => r.Title.Contains(term));
        }

        query = query.OrderByDescending(r => r.RequestedAt);
        var total = await query.CountAsync();
        var entities = await query
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync();

        var items = new List<MediaRequestDto>(entities.Count);
        var changed = false;
        foreach (var r in entities)
        {
            // Backfill title/poster if missing for better UI
            if ((string.IsNullOrWhiteSpace(r.Title) || r.Title.StartsWith("Item #")) || string.IsNullOrWhiteSpace(r.PosterUrl))
            {
                try
                {
                    var request = ToDto(r);
                    var d = request.MediaRef is { IsValid: true } mediaRef
                        ? await _metadata.GetDetailsAsync(mediaRef)
                        : await _metadata.GetDetailsAsync(r.MediaId, r.MediaType);
                    if (d is not null)
                    {
                        if (!string.IsNullOrWhiteSpace(d.Title) && (string.IsNullOrWhiteSpace(r.Title) || r.Title.StartsWith("Item #")))
                        { r.Title = d.Title; changed = true; }
                        if (string.IsNullOrWhiteSpace(r.PosterUrl) && !string.IsNullOrWhiteSpace(d.PosterUrl))
                        { r.PosterUrl = d.PosterUrl; changed = true; }
                    }
                }
                catch { /* ignore */ }
            }

            items.Add(ToDto(r));
        }
        if (changed)
        {
            try { await _db.SaveChangesAsync(); } catch { /* non-fatal */ }
        }
        return new PagedResult<MediaRequestDto>
        {
            Items = items,
            TotalCount = total,
            PageNumber = pageNumber,
            PageSize = pageSize
        };
    }

    private static PagedResult<MediaRequestDto> EmptyRequestPage(MediaFilterDto filter) => new()
    {
        PageNumber = Math.Max(1, filter.PageNumber),
        PageSize = Math.Clamp(filter.PageSize, 1, 200)
    };

    public async Task<bool> IsInWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var actor = await GetActorAsync();
        return await OwnedWatchlist(_db.Watchlist.AsNoTracking(), actor)
            .AnyAsync(w => w.MediaId == mediaId && w.MediaType == mediaType);
    }

    public async Task<MediaRequestResult> RequestMediaAsync(int mediaId, MediaType mediaType, int? qualityProfileId = null)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        if (actor.UserId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        // Keep this compatibility overload safe for older UI/API callers. A bare TV request means the
        // entire series everywhere else, so it must also inherit the administrator's monitoring default;
        // otherwise the initial aired episodes download but future episodes are silently forgotten.
        var monitor = mediaType == MediaType.TvShow
                      && (await _downloadPreferences.GetAsync()).AutoMonitorEntireSeriesRequests;
        return await CreateRequestCoreAsync(actor.UserId.Value, actor.Username, actor.IsAdmin, mediaId, mediaType,
            monitored: monitor, qualityProfileId: qualityProfileId);
    }

    public async Task<MediaRequestResult> RequestMediaAsync(MediaRef mediaRef, MediaRequestScope? scope = null,
        int? qualityProfileId = null)
    {
        if (!mediaRef.IsValid) return new MediaRequestResult { Success = false, ErrorMessage = "Invalid media identity" };
        var module = _mediaModules.Get(mediaRef.MediaType);
        if (!module.Enabled)
            return new MediaRequestResult { Success = false, ErrorMessage = module.UnavailableReason ?? "This media type is disabled" };
        if (mediaRef.MediaType == MediaType.Music && !(await _musicSettings.GetAsync()).RequestsEnabled)
            return new MediaRequestResult { Success = false, ErrorMessage = "Music requests are disabled by the administrator" };
        var requestedScope = scope?.Kind ?? mediaRef.Kind.DefaultScope();
        if (!module.Kinds.Contains(mediaRef.Kind) || !module.RequestScopes.Contains(requestedScope))
            return new MediaRequestResult { Success = false, ErrorMessage = "That request scope is not supported for this media type" };
        if (requestedScope == RequestScopeKind.Seasons && scope?.Seasons.Count is not > 0)
            return new MediaRequestResult { Success = false, ErrorMessage = "No seasons selected" };
        if (requestedScope == RequestScopeKind.Episodes && scope?.Episodes.Count is not > 0)
            return new MediaRequestResult { Success = false, ErrorMessage = "No episodes selected" };
        if (!mediaRef.TryGetTmdbId(out var tmdbId))
            return await CreateProviderRequestForCurrentUserAsync(mediaRef, requestedScope, qualityProfileId);

        return requestedScope switch
        {
            RequestScopeKind.Seasons => await RequestSeasonsAsync(tmdbId, mediaRef.MediaType, scope!.Seasons, qualityProfileId),
            RequestScopeKind.Episodes => await RequestEpisodesAsync(tmdbId, mediaRef.MediaType,
                scope!.Episodes.Select(x => (x.Season, x.Episode)).ToList(), qualityProfileId),
            RequestScopeKind.Series => await RequestSeriesAsync(tmdbId, mediaRef.MediaType, qualityProfileId),
            _ => await RequestMediaAsync(tmdbId, mediaRef.MediaType, qualityProfileId)
        };
    }

    /// <summary>Request specific seasons of a TV show (empty list ⇒ the whole series).</summary>
    public async Task<MediaRequestResult> RequestSeasonsAsync(int mediaId, MediaType mediaType, List<int> seasons, int? qualityProfileId = null)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        if (actor.UserId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var allSeasons = seasons is not { Count: > 0 };
        return await CreateRequestCoreAsync(actor.UserId.Value, actor.Username, actor.IsAdmin, mediaId, mediaType, allSeasons, seasons, qualityProfileId: qualityProfileId);
    }

    /// <summary>Request specific episodes of a TV show, e.g. [(1,1),(1,2),(2,5)].</summary>
    public async Task<MediaRequestResult> RequestEpisodesAsync(int mediaId, MediaType mediaType, List<(int season, int episode)> episodes, int? qualityProfileId = null, bool asUpgrade = false)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        if (episodes is not { Count: > 0 }) return new MediaRequestResult { Success = false, ErrorMessage = "No episodes selected" };
        if (actor.UserId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        if (asUpgrade)
        {
            if (!await _userAccess.CanRequestAsync(actor.UserId.Value, mediaType))
                return new MediaRequestResult { Success = false, ErrorMessage = "Your account is not allowed to request this media type." };
            return await QueueEpisodeUpgradeAsync(actor.UserId.Value, mediaId, mediaType, episodes, qualityProfileId);
        }
        return await CreateRequestCoreAsync(actor.UserId.Value, actor.Username, actor.IsAdmin, mediaId, mediaType, allSeasons: false, seasons: null, episodes: episodes, qualityProfileId: qualityProfileId, asUpgrade: asUpgrade);
    }

    /// <summary>
    /// Manual episode upgrades must attach to the existing available request and use the real upgrade job
    /// path. Creating a fresh ordinary request let Plex availability reconciliation complete it immediately
    /// from the old 720p episode, before the requested 1080p replacement had even been verified.
    /// </summary>
    private async Task<MediaRequestResult> QueueEpisodeUpgradeAsync(
        int userId,
        int mediaId,
        MediaType mediaType,
        IReadOnlyList<(int season, int episode)> episodes,
        int? qualityProfileId)
    {
        var wanted = episodes.Distinct().ToHashSet();
        var imported = await (from file in _db.ImportedFiles.AsNoTracking()
                              join job in _db.FulfillmentJobs.AsNoTracking() on file.FulfillmentJobId equals job.Id
                              join request in _db.MediaRequests.AsNoTracking() on job.MediaRequestId equals request.Id
                              where request.MediaId == mediaId && request.MediaType == mediaType
                                    && file.FileType == "video"
                                    && file.SeasonNumber != null && file.EpisodeNumber != null
                              select new
                              {
                                  RequestId = request.Id,
                                  request.Status,
                                  file.SeasonNumber,
                                  file.EpisodeNumber,
                                  file.DestinationPath,
                                  file.ImportedAt
                              }).ToListAsync();

        var matching = imported
            .Where(x => wanted.Contains((x.SeasonNumber!.Value, x.EpisodeNumber!.Value)))
            .ToList();

        // Prefer an Available request that owns the most selected episodes. It remains Available while its
        // upgrade runs, so ordinary availability reconciliation cannot prematurely close the upgrade job.
        var anchorId = matching
            .GroupBy(x => new { x.RequestId, x.Status })
            .OrderByDescending(g => g.Key.Status == RequestStatus.Available)
            .ThenByDescending(g => g.Select(x => (x.SeasonNumber, x.EpisodeNumber)).Distinct().Count())
            .ThenByDescending(g => g.Max(x => x.ImportedAt))
            .Select(g => (int?)g.Key.RequestId)
            .FirstOrDefault();

        if (anchorId is null)
        {
            anchorId = await _db.MediaRequests.AsNoTracking()
                .Where(r => r.MediaId == mediaId && r.MediaType == mediaType && r.Status == RequestStatus.Available)
                .OrderByDescending(r => r.AvailableAt ?? r.RequestedAt)
                .Select(r => (int?)r.Id)
                .FirstOrDefaultAsync();
        }

        if (anchorId is null)
            return new MediaRequestResult
            {
                Success = false,
                ErrorMessage = "The existing Plex episode could not be matched to a request, so no files were changed. Refresh Plex availability and try again."
            };

        var anchor = await _db.MediaRequests.FirstAsync(r => r.Id == anchorId.Value);
        var profileId = await _qualityProfiles.ResolveProfileIdAsync(
            mediaType, mediaId, genres: null, requesterChoiceId: qualityProfileId, userId: userId);
        var target = await _qualityProfiles.GetCutoffQualityAsync(profileId);
        if (target == Quality.Any)
            return new MediaRequestResult { Success = false, ErrorMessage = "The selected quality profile has no upgrade target." };

        var replacePaths = matching.Select(x => x.DestinationPath)
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        var queued = await _fulfillment.EnqueueUpgradeAsync(ToDto(anchor), target, replacePaths, wanted.ToList());
        if (!queued)
            return new MediaRequestResult { Success = false, ErrorMessage = "An upgrade for this request is already running." };

        return new MediaRequestResult
        {
            Success = true,
            RequestId = anchor.Id,
            NewStatus = anchor.Status
        };
    }

    /// <summary>
    /// Create ONE auto-approved child request covering all newly-aired missing episodes of a monitored
    /// series (grouped per season by the caller), and enqueue it. Used by SeriesMonitorService — flows
    /// through the normal one-request/one-job pipeline, so the fulfilled callback marks THIS child available
    /// (the monitored anchor is untouched). Batching the season's missing episodes into a single request
    /// (rather than one request per episode) lets the downloader satisfy them from a single season pack
    /// when no standalone episode releases exist, instead of spawning a doomed job per episode. The child
    /// is durable and reused on every reconciliation; Success means its scope is covered by existing or new
    /// live work, while JobQueued distinguishes a newly-persisted job.
    /// </summary>
    public async Task<MediaRequestResult> CreateMonitoredEpisodesAsync(int anchorRequestId, IReadOnlyList<(int season, int episode)> episodes)
    {
        if (episodes.Count == 0) return new MediaRequestResult { Success = false, ErrorMessage = "No episodes specified" };
        var anchor = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == anchorRequestId);
        if (anchor is null) return new MediaRequestResult { Success = false, ErrorMessage = "Anchor request not found" };

        var wanted = episodes.Distinct().OrderBy(e => e.season).ThenBy(e => e.episode).ToList();
        var seasons = wanted.Select(e => e.season).Distinct().ToList();
        if (seasons.Count != 1)
            return new MediaRequestResult { Success = false, ErrorMessage = "Monitored episodes must be queued one season at a time" };

        var season = seasons[0];
        var csv = string.Join(",", wanted
            .Select(e => $"S{e.season}E{e.episode}"));

        // One durable request per (anchor, season). The previous append/delete approach consumed a new row
        // on every 15-minute coverage check and, before its delete-on-no-op fix, left hundreds of Approved
        // requests with no job. Reusing this row also gives partial completion a stable place to resume.
        var child = await _db.MediaRequests.FirstOrDefaultAsync(r =>
            r.MonitoringAnchorId == anchor.Id && r.MonitoringSeasonNumber == season);

        if (child is not null)
        {
            var activeJob = await _db.FulfillmentJobs
                .Where(j => j.MediaRequestId == child.Id &&
                    (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
                     j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred))
                .OrderByDescending(j => j.Id)
                .FirstOrDefaultAsync();
            if (activeJob is not null)
            {
                // A Deferred job can live for days while new episodes air. Widen work that has not begun to
                // the current missing set and wake a Deferred search immediately; otherwise one hard-to-find
                // episode could serialize the season and strand every later episode behind it forever.
                if (activeJob.Status is FulfillmentStatus.Queued or FulfillmentStatus.Deferred
                    && !string.Equals(activeJob.RequestedEpisodesCsv, csv, StringComparison.OrdinalIgnoreCase))
                {
                    RefreshPendingMonitorScope(child, activeJob, csv, DateTime.UtcNow);
                    await _db.SaveChangesAsync();
                }

                return new MediaRequestResult
                {
                    Success = true,
                    AlreadyCovered = true,
                    RequestId = child.Id,
                    NewStatus = child.Status
                };
            }

            // The preceding job is terminal (including PartiallyCompleted). Reconcile the child to the
            // CURRENT Plex-missing set and let EnqueueAsync narrow it once more against the latest index.
            child.RequestedEpisodesCsv = csv;
            child.Status = RequestStatus.Approved;
            child.ApprovedAt = DateTime.UtcNow;
            child.AvailableAt = null;
            child.DenialReason = null;
            child.QualityProfileId = anchor.QualityProfileId ?? child.QualityProfileId;
        }
        else
        {
            child = new MediaRequestEntity
            {
                MediaIdentityId = anchor.MediaIdentityId,
                MediaId = anchor.MediaId,
                MediaType = anchor.MediaType,
                RequestScopeKind = RequestScopeKind.Episodes,
                Title = anchor.Title,
                PosterUrl = anchor.PosterUrl,
                Status = RequestStatus.Approved,
                RequestedAt = DateTime.UtcNow,
                ApprovedAt = DateTime.UtcNow,
                RequestedBy = anchor.RequestedBy,
                RequestedByUserId = anchor.RequestedByUserId,
                RequestAllSeasons = false,
                RequestedEpisodesCsv = csv,
                Monitored = false,
                MonitorMode = MonitorMode.None,
                MonitoringAnchorId = anchor.Id,
                MonitoringSeasonNumber = season,
                // Inherit the anchor's profile rather than re-resolving. A newly-aired episode of a series the
                // user asked for in 1080p has to arrive in 1080p; re-resolving would silently apply whatever the
                // assignment rules say today, which is not what was agreed when the series was requested.
                QualityProfileId = anchor.QualityProfileId
                    ?? await _qualityProfiles.ResolveProfileIdAsync(anchor.MediaType, anchor.MediaId, null,
                        requesterChoiceId: null, userId: anchor.RequestedByUserId)
            };
            _db.MediaRequests.Add(child);
        }
        await _db.SaveChangesAsync();

        var queued = _config.GetValue<bool>("Fulfillment:Enabled")
                     && await _fulfillment.EnqueueAsync(ToDto(child));

        if (!queued)
        {
            return new MediaRequestResult
            {
                Success = false,
                JobQueued = false,
                ErrorMessage = "No fulfillment job was created for monitored episodes right now."
            };
        }

        return new MediaRequestResult { Success = true, JobQueued = true, RequestId = child.Id, NewStatus = child.Status };
    }

    internal static void RefreshPendingMonitorScope(
        MediaRequestEntity child, FulfillmentJobEntity job, string episodesCsv, DateTime now)
    {
        child.RequestedEpisodesCsv = episodesCsv;
        job.RequestedEpisodesCsv = episodesCsv;
        job.SeasonTargetsJson = null;
        if (job.Status == FulfillmentStatus.Deferred)
        {
            job.Status = FulfillmentStatus.Queued;
            job.NextRetryAt = null;
            job.LastUpdatedAt = now;
        }
    }

    /// <summary>Compatibility entry point; all music requests now use the canonical provider-neutral path.</summary>
    public async Task<MediaRequestResult> RequestMusicAsync(
        string externalId, string source, string title, string? posterUrl = null)
    {
        var mediaRef = MediaRef.FromExternal(
            string.IsNullOrWhiteSpace(source) ? "musicbrainz" : source,
            externalId, MediaType.Music, MediaKind.Album);
        if (!mediaRef.IsValid)
            return new MediaRequestResult { Success = false, ErrorMessage = "Invalid music identity" };
        var module = _mediaModules.Get(MediaType.Music);
        if (!module.Enabled)
            return new MediaRequestResult
                { Success = false, ErrorMessage = module.UnavailableReason ?? "Music requests are disabled" };
        if (!(await _musicSettings.GetAsync()).RequestsEnabled)
            return new MediaRequestResult { Success = false, ErrorMessage = "Music requests are disabled by the administrator" };
        return await CreateProviderRequestForCurrentUserAsync(
            mediaRef, RequestScopeKind.Album, null, title, posterUrl);
    }

    /// <summary>Request an entire series. Whether it's then monitored for new episodes as they air is an
    /// admin-configured default (<see cref="DownloadPreferencesDto.AutoMonitorEntireSeriesRequests"/>), not
    /// a per-request user choice.</summary>
    public async Task<MediaRequestResult> RequestSeriesAsync(int mediaId, MediaType mediaType, int? qualityProfileId = null)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username)) return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        if (actor.UserId is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var monitor = (await _downloadPreferences.GetAsync()).AutoMonitorEntireSeriesRequests;
        return await CreateRequestCoreAsync(actor.UserId.Value, actor.Username, actor.IsAdmin, mediaId, mediaType, allSeasons: true, seasons: null, episodes: null, monitored: monitor, qualityProfileId: qualityProfileId);
    }

    /// <summary>Create a request on behalf of an explicit user (used by the Discord bridge, which has no cookie session).</summary>
    public async Task<MediaRequestResult> RequestMediaForUserAsync(int userId, int mediaId, MediaType mediaType)
    {
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var access = await _userAccess.GetAccessAsync(userId);
        if (!access.IsActive) return AccountUnavailable(access);
        var isAdmin = access.IsAdmin;
        var monitor = mediaType == MediaType.TvShow
                      && (await _downloadPreferences.GetAsync()).AutoMonitorEntireSeriesRequests;
        return await CreateRequestCoreAsync(userId, user.Username, isAdmin, mediaId, mediaType,
            allSeasons: true, seasons: null, episodes: null, monitored: monitor);
    }

    public async Task<MediaRequestResult> RequestMediaForUserAsync(int userId, MediaRef mediaRef,
        MediaRequestScope? scope = null, int? qualityProfileId = null)
    {
        if (!mediaRef.IsValid) return new MediaRequestResult { Success = false, ErrorMessage = "Invalid media identity" };
        var module = _mediaModules.Get(mediaRef.MediaType);
        if (!module.Enabled)
            return new MediaRequestResult { Success = false, ErrorMessage = module.UnavailableReason ?? "This media type is disabled" };
        if (mediaRef.MediaType == MediaType.Music && !(await _musicSettings.GetAsync()).RequestsEnabled)
            return new MediaRequestResult { Success = false, ErrorMessage = "Music requests are disabled by the administrator" };
        var requestScope = scope?.Kind ?? mediaRef.Kind.DefaultScope();
        if (!module.Kinds.Contains(mediaRef.Kind) || !module.RequestScopes.Contains(requestScope))
            return new MediaRequestResult { Success = false, ErrorMessage = "That request scope is not supported for this media type" };
        if (requestScope == RequestScopeKind.Seasons && scope?.Seasons.Count is not > 0)
            return new MediaRequestResult { Success = false, ErrorMessage = "No seasons selected" };
        if (requestScope == RequestScopeKind.Episodes && scope?.Episodes.Count is not > 0)
            return new MediaRequestResult { Success = false, ErrorMessage = "No episodes selected" };
        var user = await _db.Users.FirstOrDefaultAsync(u => u.Id == userId);
        if (user is null) return new MediaRequestResult { Success = false, ErrorMessage = "User not found" };
        var access = await _userAccess.GetAccessAsync(userId);
        if (!access.IsActive) return AccountUnavailable(access);
        var isAdmin = access.IsAdmin;
        if (!mediaRef.TryGetTmdbId(out var tmdbId))
            return await CreateProviderRequestCoreAsync(userId, user.Username, isAdmin, mediaRef,
                requestScope, qualityProfileId);
        var seasons = requestScope == RequestScopeKind.Seasons ? scope?.Seasons : null;
        var episodes = requestScope == RequestScopeKind.Episodes
            ? scope?.Episodes.Select(x => (x.Season, x.Episode)).ToList()
            : null;
        var monitor = mediaRef.MediaType == MediaType.TvShow && requestScope == RequestScopeKind.Series
                      && (await _downloadPreferences.GetAsync()).AutoMonitorEntireSeriesRequests;
        return await CreateRequestCoreAsync(userId, user.Username, isAdmin, tmdbId, mediaRef.MediaType,
            allSeasons: requestScope == RequestScopeKind.Series, seasons: seasons, episodes: episodes,
            monitored: monitor, qualityProfileId: qualityProfileId);
    }

    private static MediaRequestResult AccountUnavailable(UserAccessSnapshot access) => new()
    {
        Success = false,
        ErrorMessage = access.AccountStatus == UserAccountStatus.Suspended
            ? $"Your account is temporarily suspended{(access.SuspendedUntil is DateTime until ? $" until {until:u}" : string.Empty)}."
            : "Your account is disabled. Contact an administrator."
    };

    private async Task<MediaRequestResult> CreateProviderRequestCoreAsync(
        int userId,
        string username,
        bool isAdmin,
        MediaRef mediaRef,
        RequestScopeKind requestScope,
        int? qualityProfileId,
        string? fallbackTitle = null,
        string? fallbackPoster = null)
    {
        var identity = await _mediaIdentities.ResolveAsync(mediaRef);
        var duplicate = await _db.MediaRequests.AnyAsync(r =>
            r.MediaIdentityId == identity.Id && r.RequestScopeKind == requestScope &&
            (r.RequestedByUserId == userId &&
                (r.Status == RequestStatus.Pending || r.Status == RequestStatus.Approved
                 || r.Status == RequestStatus.Processing || r.Status == RequestStatus.Searching)
             || r.Status == RequestStatus.Available));
        if (duplicate)
            return new MediaRequestResult { Success = false, ErrorMessage = "This item is already requested or available." };

        if (!await _userAccess.CanRequestAsync(userId, mediaRef.MediaType))
            return new MediaRequestResult { Success = false, ErrorMessage = "Your account is not allowed to request this media type." };
        if (!isAdmin && !await CheckLimitsCoreAsync(userId, mediaRef.MediaType))
            return new MediaRequestResult
                { Success = false, ErrorMessage = "You've reached your request limit for this media type." };

        MediaDetailDto? detail = null;
        try { detail = await _metadata.GetDetailsAsync(mediaRef); }
        catch { /* durable identity still makes a later metadata/enqueue retry possible */ }

        if (mediaRef.MediaType == MediaType.Music && detail is not null)
        {
            var present = await _plexMusic.VerifyAsync(new PlexVerificationRequest(
                MediaType.Music, mediaRef.Kind, mediaRef.Provider, mediaRef.Id,
                mediaRef.Kind == MediaKind.Artist ? detail.Title : detail.Music?.ArtistCredit,
                mediaRef.Kind == MediaKind.Album ? detail.Title
                    : mediaRef.Kind == MediaKind.Track ? detail.Music?.AlbumTitle : null,
                mediaRef.Kind == MediaKind.Track ? detail.Title : null,
                detail.Music?.AlbumTitles,
                detail.Music?.Tracks.Select(x => x.Title).ToList(),
                detail.Music?.Tracks.FirstOrDefault()?.DurationMs));
            if (present.Available)
                return new MediaRequestResult { Success = false, ErrorMessage = "This music is already available on Plex." };
        }

        var autoApprove = (await _userAccess.GetAccessAsync(userId)).AutoApprove;
        var entity = new MediaRequestEntity
        {
            MediaIdentityId = identity.Id,
            MediaId = 0,
            MediaType = mediaRef.MediaType,
            RequestScopeKind = requestScope,
            ExternalId = mediaRef.Id,
            ExternalSource = mediaRef.Provider,
            Title = string.IsNullOrWhiteSpace(detail?.Title)
                ? !string.IsNullOrWhiteSpace(fallbackTitle) ? fallbackTitle.Trim() : mediaRef.Id
                : detail.Title,
            PosterUrl = detail?.PosterUrl ?? fallbackPoster,
            Status = autoApprove ? RequestStatus.Approved : RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = autoApprove ? DateTime.UtcNow : null,
            RequestedBy = username,
            RequestedByUserId = userId,
            RequestAllSeasons = false,
            QualityProfileId = await _qualityProfiles.ResolveProfileIdAsync(
                mediaRef.MediaType, 0, detail?.Genres, qualityProfileId, userId)
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        if (!saved) return new MediaRequestResult { Success = false, ErrorMessage = "The request could not be saved." };

        var dto = ToDto(entity);
        if (autoApprove)
        {
            await _notify.RequestApprovedAsync(dto);
            if (_config.GetValue<bool>("Fulfillment:Enabled"))
                await _fulfillment.EnqueueAsync(dto);
        }
        else
        {
            await _notify.RequestCreatedAsync(dto);
        }
        return new MediaRequestResult
        {
            Success = true,
            RequestId = entity.Id,
            NewStatus = entity.Status,
            RequestScope = requestScope
        };
    }

    private async Task<MediaRequestResult> CreateProviderRequestForCurrentUserAsync(
        MediaRef mediaRef,
        RequestScopeKind requestScope,
        int? qualityProfileId,
        string? fallbackTitle = null,
        string? fallbackPoster = null)
    {
        var actor = await GetActorAsync();
        if (string.IsNullOrWhiteSpace(actor.Username))
            return new MediaRequestResult { Success = false, ErrorMessage = "Not authenticated" };
        return actor.UserId is null
            ? new MediaRequestResult { Success = false, ErrorMessage = "User not found" }
            : await CreateProviderRequestCoreAsync(actor.UserId.Value, actor.Username, actor.IsAdmin, mediaRef,
                requestScope, qualityProfileId, fallbackTitle, fallbackPoster);
    }

    private async Task<MediaRequestResult> CreateRequestCoreAsync(int userId, string username, bool isAdmin, int mediaId, MediaType mediaType, bool allSeasons = true, List<int>? seasons = null, List<(int season, int episode)>? episodes = null, bool monitored = false, int? qualityProfileId = null, bool asUpgrade = false)
    {
        // Per-user duplicate check: a second user requesting the same title joins it rather than
        // being blocked. Only the same user re-requesting an already-covered scope is rejected — for TV
        // this is season/episode-scope aware, so e.g. an active season-1 request doesn't block a season-3
        // request for the same show, and a season is only "already available" when it's genuinely complete
        // (not just the instant any single episode landed).
        // An upgrade asks for something we deliberately already have, so the duplicate/already-available
        // guard is exactly what must NOT fire. Without this bypass there was no way to ask for a better
        // copy of an episode at all: the UI greyed it out, and the service would have refused anyway.
        if (!asUpgrade)
        {
            var (blocked, blockMessage) = await CheckScopeConflictAsync(userId, mediaId, mediaType, allSeasons, seasons, episodes);
            if (blocked) return new MediaRequestResult { Success = false, ErrorMessage = blockMessage };
        }

        if (!await _userAccess.CanRequestAsync(userId, mediaType))
            return new MediaRequestResult { Success = false, ErrorMessage = "Your account is not allowed to request this media type." };
        if (!isAdmin && !await CheckLimitsCoreAsync(userId, mediaType))
            return new MediaRequestResult { Success = false, ErrorMessage = "You've reached your request limit for this media type." };

        // Admins, and users flagged AutoApprove, skip the pending queue.
        var autoApprove = (await _userAccess.GetAccessAsync(userId)).AutoApprove;

        // Enrich with metadata for nice UI
        string title = $"Item #{mediaId}";
        string? poster = null;
        // Genres are picked up here too: profile assignment rules can match on genre, so resolving the
        // profile below needs them. Best-effort, same as the title — a metadata outage falls back to the
        // rules that don't need genres, not to no profile at all.
        List<string>? genres = null;
        try
        {
            var details = await _metadata.GetDetailsAsync(mediaId, mediaType);
            if (details is not null)
            {
                title = string.IsNullOrWhiteSpace(details.Title) ? title : details.Title;
                poster = details.PosterUrl;
                genres = details.Genres;
            }
        }
        catch { /* best-effort */ }

        var identity = await _mediaIdentities.ResolveAsync(MediaRef.FromTmdb(mediaId, mediaType));
        var requestScope = mediaType is MediaType.TvShow or MediaType.Anime
            ? episodes is { Count: > 0 } ? RequestScopeKind.Episodes
            : seasons is { Count: > 0 } ? RequestScopeKind.Seasons
            : RequestScopeKind.Series
            : RequestScopeKind.Title;
        var entity = new MediaRequestEntity
        {
            MediaIdentityId = identity.Id,
            MediaId = mediaId,
            MediaType = mediaType,
            RequestScopeKind = requestScope,
            Title = title,
            PosterUrl = poster,
            Status = autoApprove ? RequestStatus.Approved : RequestStatus.Pending,
            RequestedAt = DateTime.UtcNow,
            ApprovedAt = autoApprove ? DateTime.UtcNow : null,
            RequestedBy = username,
            RequestedByUserId = userId,
            // TV selection: episodes > seasons > whole series. Movies are always "all".
            RequestAllSeasons = mediaType == MediaType.TvShow
                ? (episodes is not { Count: > 0 } && seasons is not { Count: > 0 } && allSeasons)
                : true,
            RequestedSeasonsCsv = mediaType == MediaType.TvShow && seasons is { Count: > 0 }
                ? string.Join(",", seasons.Distinct().OrderBy(x => x))
                : null,
            RequestedEpisodesCsv = mediaType == MediaType.TvShow && episodes is { Count: > 0 }
                ? string.Join(",", episodes.Distinct().OrderBy(e => e.season).ThenBy(e => e.episode).Select(e => $"S{e.season}E{e.episode}"))
                : null,
            Monitored = mediaType == MediaType.TvShow && monitored,
            MonitorMode = mediaType == MediaType.TvShow && monitored ? MonitorMode.AllEpisodes : MonitorMode.None,
            MonitoredSince = mediaType == MediaType.TvShow && monitored ? DateTime.UtcNow : null,
            // Resolved here rather than left null for the queue to work out later: the request row is what
            // the whole system reads to decide what "good enough" means for this title, and a null there
            // meant the answer depended on when you asked. ResolveProfileIdAsync re-validates the
            // requester's pick against what they may actually select, so an unauthorised id is ignored
            // rather than trusted.
            QualityProfileId = await _qualityProfiles.ResolveProfileIdAsync(mediaType, mediaId, genres,
                qualityProfileId, userId)
        };
        _db.MediaRequests.Add(entity);
        var saved = await _db.SaveChangesAsync() > 0;
        if (saved)
        {
            var dto = ToDto(entity);
            if (autoApprove)
            {
                // Straight to approved: notify + hand off to fulfillment (if a downloader is wired up).
                await _notify.RequestApprovedAsync(dto);
                // force: an upgrade's whole point is to re-fetch content already on Plex, and the enqueue
                // path skips anything it finds there unless told otherwise.
                if (_config.GetValue<bool>("Fulfillment:Enabled")) await _fulfillment.EnqueueAsync(dto, force: asUpgrade);
            }
            else
            {
                await _notify.RequestCreatedAsync(dto);
            }
        }
        return new MediaRequestResult
        {
            Success = saved,
            RequestId = entity.Id,
            NewStatus = entity.Status,
            RequestScope = requestScope,
            MonitorsFutureReleases = entity.Monitored
        };
    }

    /// <summary>
    /// Whether a new request would be entirely redundant: for movies, the classic status-based checks;
    /// for TV, a season-scope-aware check that only blocks when the new request's exact scope is already
    /// fully covered by the same user's other active requests, or is already fully available on Plex
    /// right now (not a sticky terminal status that can never un-stick).
    /// </summary>
    private async Task<(bool blocked, string? message)> CheckScopeConflictAsync(
        int userId, int mediaId, MediaType mediaType, bool allSeasons, List<int>? seasons, List<(int season, int episode)>? episodes)
    {
        if (mediaType != MediaType.TvShow)
        {
            var alreadyMine = await _db.MediaRequests.AnyAsync(r =>
                r.MediaId == mediaId && r.MediaType == mediaType && r.RequestedByUserId == userId &&
                (r.Status == RequestStatus.Pending || r.Status == RequestStatus.Approved || r.Status == RequestStatus.Processing));
            if (alreadyMine) return (true, "You've already requested this title.");

            var alreadyAvailable = await _db.MediaRequests.AnyAsync(r =>
                r.MediaId == mediaId && r.MediaType == mediaType && r.Status == RequestStatus.Available);
            if (alreadyAvailable) return (true, "This title is already available.");

            return (false, null);
        }

        var newScope = await ResolveSeasonScopeAsync(mediaId, allSeasons, seasons, episodes);
        if (newScope.Count == 0) return (false, null); // couldn't resolve scope (metadata miss) — don't guess, allow it

        var myActive = await _db.MediaRequests
            .Where(r => r.MediaId == mediaId && r.MediaType == mediaType && r.RequestedByUserId == userId)
            .ToListAsync();

        // Episode requests are compared at episode granularity. The former season-only comparison made an
        // active S09E03 request block a missing S09E01 as "already requested", even though the jobs could
        // satisfy completely different files.
        if (episodes is { Count: > 0 })
        {
            var wantedEpisodes = episodes.ToHashSet();
            var coveredEpisodes = new HashSet<(int season, int episode)>();
            foreach (var request in myActive.Where(r => IsActiveStatus(r.Status)))
            {
                if (request.RequestAllSeasons || (string.IsNullOrWhiteSpace(request.RequestedEpisodesCsv)
                                                  && string.IsNullOrWhiteSpace(request.RequestedSeasonsCsv)))
                {
                    coveredEpisodes.UnionWith(wantedEpisodes);
                    continue;
                }

                if (!string.IsNullOrWhiteSpace(request.RequestedSeasonsCsv))
                {
                    var coveredSeasons = ParseSeasons(request.RequestedSeasonsCsv);
                    coveredEpisodes.UnionWith(wantedEpisodes.Where(e => coveredSeasons.Contains(e.season)));
                    continue;
                }

                coveredEpisodes.UnionWith(ParseEpisodes(request.RequestedEpisodesCsv));
            }

            if (wantedEpisodes.All(coveredEpisodes.Contains))
                return (true, "You've already requested this episode.");

            var availableEpisodes = await _seasonAvailability.GetPlexEpisodesAsync(mediaId);
            if (wantedEpisodes.All(e => availableEpisodes.TryGetValue(e.season, out var have) && have.Contains(e.episode)))
                return (true, "This episode is already available.");

            return (false, null);
        }

        var covered = new HashSet<int>();
        foreach (var r in myActive.Where(r => IsActiveStatus(r.Status)))
            covered.UnionWith(await ResolveRequestScopeAsync(r));
        if (newScope.All(covered.Contains))
            return (true, "You've already requested this title/season.");

        var evaluation = await _seasonAvailability.EvaluateAsync(mediaId);
        if (newScope.All(s => evaluation.TryGetValue(s, out var sc) && sc.Complete))
            return (true, "This season is already fully available.");

        return (false, null);
    }

    // The set of season numbers a new request would target: explicit episodes/seasons, or (whole series)
    // every season TMDB currently knows about.
    private async Task<HashSet<int>> ResolveSeasonScopeAsync(int mediaId, bool wholeSeries, List<int>? seasons, List<(int season, int episode)>? episodes)
    {
        if (episodes is { Count: > 0 }) return episodes.Select(e => e.season).ToHashSet();
        if (seasons is { Count: > 0 }) return seasons.ToHashSet();
        if (!wholeSeries) return new HashSet<int>();
        var detail = await _metadata.GetDetailsAsync(mediaId, MediaType.TvShow);
        return (detail?.Seasons?.Where(s => s.SeasonNumber > 0).Select(s => s.SeasonNumber) ?? Enumerable.Empty<int>()).ToHashSet();
    }

    // The set of season numbers an EXISTING request row targets, same rules as above.
    private async Task<HashSet<int>> ResolveRequestScopeAsync(MediaRequestEntity r)
    {
        if (!string.IsNullOrWhiteSpace(r.RequestedEpisodesCsv))
            return ParseEpisodeSeasons(r.RequestedEpisodesCsv);
        if (!string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv))
            return r.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : -1).Where(n => n >= 0).ToHashSet();
        var detail = await _metadata.GetDetailsAsync(r.MediaId, MediaType.TvShow);
        return (detail?.Seasons?.Where(s => s.SeasonNumber > 0).Select(s => s.SeasonNumber) ?? Enumerable.Empty<int>()).ToHashSet();
    }

    private static HashSet<int> ParseEpisodeSeasons(string csv)
    {
        var seasons = new HashSet<int>();
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
            if (m.Success) seasons.Add(int.Parse(m.Groups[1].Value));
        }
        return seasons;
    }

    private static HashSet<int> ParseSeasons(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : -1)
            .Where(n => n >= 0)
            .ToHashSet();

    private static HashSet<(int season, int episode)> ParseEpisodes(string? csv)
    {
        var episodes = new HashSet<(int season, int episode)>();
        if (string.IsNullOrWhiteSpace(csv)) return episodes;
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var match = System.Text.RegularExpressions.Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
            if (match.Success)
                episodes.Add((int.Parse(match.Groups[1].Value), int.Parse(match.Groups[2].Value)));
        }
        return episodes;
    }

    /// <summary>A specific user's requests, newest first (used by the Discord bridge /request status).</summary>
    public async Task<List<MediaRequestDto>> GetRequestsForUserAsync(int userId, int take = 25)
    {
        var rows = await _db.MediaRequests
            .Where(r => r.RequestedByUserId == userId)
            .OrderByDescending(r => r.RequestedAt)
            .Take(take)
            .ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    private static MediaRequestDto ToDto(MediaRequestEntity r) => new()
    {
        Id = r.Id,
        MediaId = r.MediaId,
        MediaType = r.MediaType,
        Title = r.Title,
        PosterUrl = r.PosterUrl,
        Status = r.Status,
        RequestedAt = r.RequestedAt,
        ApprovedAt = r.ApprovedAt,
        AvailableAt = r.AvailableAt,
        DenialReason = r.DenialReason,
        RequestNote = r.RequestNote,
        RequestAllSeasons = r.RequestAllSeasons,
        RequestedEpisodesCsv = r.RequestedEpisodesCsv,
        Monitored = r.Monitored,
        ExternalId = r.ExternalId,
        ExternalSource = r.ExternalSource,
        MediaRef = !string.IsNullOrWhiteSpace(r.ExternalId)
            ? PlexRequestsHosted.Shared.Media.MediaRef.FromExternal(r.ExternalSource ?? "external", r.ExternalId,
                r.MediaType, r.RequestScopeKind.ToMediaKind(r.MediaType))
            : PlexRequestsHosted.Shared.Media.MediaRef.FromTmdb(r.MediaId, r.MediaType),
        RequestScopeKind = r.RequestScopeKind,
        RequestedByUserId = r.RequestedByUserId ?? 0,
        RequestedByUsername = r.RequestedBy ?? string.Empty,
        RequestedSeasons = string.IsNullOrWhiteSpace(r.RequestedSeasonsCsv)
            ? new List<int>()
            : r.RequestedSeasonsCsv.Split(',').Select(s => int.TryParse(s, out var n) ? (int?)n : null).Where(n => n.HasValue).Select(n => n!.Value).ToList()
    };

    public async Task<bool> RemoveFromWatchlistAsync(int mediaId, MediaType mediaType)
    {
        var actor = await GetActorAsync();
        var item = await OwnedWatchlist(_db.Watchlist, actor)
            .FirstOrDefaultAsync(w => w.MediaId == mediaId && w.MediaType == mediaType);
        if (item == null) return false;
        _db.Watchlist.Remove(item);
        return await _db.SaveChangesAsync() > 0;
    }

    public async Task<bool> CheckRequestLimitsAsync(MediaType mediaType)
    {
        var actor = await GetActorAsync();
        if (actor.UserId is null) return false;
        if (!await _userAccess.CanRequestAsync(actor.UserId.Value, mediaType)) return false;
        if (actor.IsAdmin) return true;
        return await CheckLimitsCoreAsync(actor.UserId.Value, mediaType);
    }

    private async Task<bool> CheckLimitsCoreAsync(int userId, MediaType mediaType)
    {
        var userAccess = await _userAccess.GetAccessAsync(userId);
        return await _userQuotas.HasCapacityAsync(userId, mediaType, userAccess);
    }

    public async Task<bool> ApproveRequestAsync(int requestId, string? note = null)
    {
        if (!(await GetActorAsync()).IsAdmin) return false;
        return await ApproveCoreAsync(requestId, note);
    }

    /// <summary>Approve without a cookie auth check — the caller (e.g. Discord bridge) has already verified admin.</summary>
    public Task<bool> ApproveRequestAsAdminAsync(int requestId, string? note = null) => ApproveCoreAsync(requestId, note);

    private async Task<bool> ApproveCoreAsync(int requestId, string? note)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Approved;
        req.ApprovedAt = DateTime.UtcNow;
        req.DenialReason = null;
        if (!string.IsNullOrWhiteSpace(note)) req.RequestNote = note;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok)
        {
            var dto = ToDto(req);
            await _notify.RequestApprovedAsync(dto);
            // Hand off to the fulfillment pipeline when a downloader is wired up.
            if (_config.GetValue<bool>("Fulfillment:Enabled"))
                await _fulfillment.EnqueueAsync(dto);
        }
        return ok;
    }

    public async Task<bool> DenyRequestAsync(int requestId, string reason)
    {
        if (!(await GetActorAsync()).IsAdmin) return false;
        return await DenyCoreAsync(requestId, reason);
    }

    /// <summary>Deny without a cookie auth check — the caller has already verified admin.</summary>
    public Task<bool> DenyRequestAsAdminAsync(int requestId, string reason) => DenyCoreAsync(requestId, reason);

    private async Task<bool> DenyCoreAsync(int requestId, string reason)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Rejected;
        req.DenialReason = reason;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok) await _notify.RequestRejectedAsync(ToDto(req));
        return ok;
    }

    public async Task<bool> MarkAvailableAsync(int requestId)
    {
        if (!(await GetActorAsync()).IsAdmin) return false;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == requestId);
        if (req is null) return false;
        req.Status = RequestStatus.Available;
        req.AvailableAt = DateTime.UtcNow;
        var ok = await _db.SaveChangesAsync() > 0;
        if (ok)
        {
            await _notify.RequestAvailableAsync(ToDto(req));
        }
        return ok;
    }

    public async Task<Dictionary<string, RequestStatus>> GetMyRequestStatusesAsync(IEnumerable<MediaRef> items)
    {
        var actor = await GetActorAsync();
        var refs = items.Where(x => x is { IsValid: true })
            .DistinctBy(x => x.StableKey, StringComparer.OrdinalIgnoreCase).ToList();
        if (refs.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        var stableKeys = refs.Select(x => x.StableKey).ToList();
        var identities = await _db.MediaIdentities.AsNoTracking()
            .Where(x => stableKeys.Contains(x.StableKey))
            .Select(x => new { x.Id, x.StableKey })
            .ToListAsync();
        var identityByInputKey = identities.ToDictionary(x => x.StableKey, x => x.Id,
            StringComparer.OrdinalIgnoreCase);

        // A card may arrive through a secondary provider alias. Resolve those aliases to the same request
        // row instead of making the UI status depend on whichever provider originally created the identity.
        var unresolved = refs.Where(x => !identityByInputKey.ContainsKey(x.StableKey)).ToList();
        if (unresolved.Count > 0)
        {
            var providers = unresolved.Select(x => MediaRef.NormalizeProvider(x.Provider)).Distinct().ToList();
            var externalIds = unresolved.Select(x => MediaRef.NormalizeId(x.Provider, x.Id)).Distinct().ToList();
            var aliases = await _db.MediaExternalIdentifiers.AsNoTracking()
                .Where(x => providers.Contains(x.Provider) && externalIds.Contains(x.ExternalId))
                .Select(x => new { x.MediaIdentityId, x.Provider, x.ExternalId, x.MediaType, x.Kind })
                .ToListAsync();
            var wanted = unresolved.Select(x => x.StableKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var alias in aliases)
            {
                var key = MediaRef.FromExternal(alias.Provider, alias.ExternalId, alias.MediaType, alias.Kind).StableKey;
                if (wanted.Contains(key)) identityByInputKey[key] = alias.MediaIdentityId;
            }
        }
        if (identityByInputKey.Count == 0) return new(StringComparer.OrdinalIgnoreCase);

        var identityIds = identityByInputKey.Values.Distinct().ToList();
        IQueryable<MediaRequestEntity> query = OwnedRequests(_db.MediaRequests, actor)
            .Where(x => x.MediaIdentityId != null && identityIds.Contains(x.MediaIdentityId.Value));
        var requests = await query.Select(x => new { IdentityId = x.MediaIdentityId!.Value, x.Status }).ToListAsync();
        var keysByIdentity = identityByInputKey.GroupBy(x => x.Value)
            .ToDictionary(x => x.Key, x => x.Select(y => y.Key).ToList());
        var result = new Dictionary<string, RequestStatus>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in requests)
        {
            if (!keysByIdentity.TryGetValue(row.IdentityId, out var keys)) continue;
            foreach (var key in keys)
                result[key] = result.TryGetValue(key, out var existing)
                    ? BestStatus(existing, row.Status) : row.Status;
        }
        return result;
    }

    private static RequestStatus BestStatus(RequestStatus a, RequestStatus b)
    {
        // Preserve the furthest meaningful lifecycle state when duplicate historical rows exist.
        int Rank(RequestStatus s) => s switch
        {
            RequestStatus.Available => 8,
            RequestStatus.PartiallyAvailable => 7,
            RequestStatus.Processing => 6,
            RequestStatus.Approved => 5,
            RequestStatus.Searching => 4,
            RequestStatus.Pending => 3,
            RequestStatus.Cancelled => 2,
            RequestStatus.Rejected => 1,
            _ => 0
        };
        return Rank(a) >= Rank(b) ? a : b;
    }

    private sealed record RequestActor(int? UserId, string Username, bool IsAdmin);
}
