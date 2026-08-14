using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Services.Jobs;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IFulfillmentQueue"/>. SQLite is single-writer, so claims are safe
/// without extra locking at our scale; swap for Redis/RabbitMQ later without touching callers.
/// </summary>
public class FulfillmentQueue(AppDbContext db, IMediaMetadataProvider metadata, IQualityProfileService profiles, ICustomFormatService formats, ISeasonAvailabilityEvaluator seasonEvaluator, ILogger<FulfillmentQueue> logger) : IFulfillmentQueue
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;
    private readonly IQualityProfileService _profiles = profiles;
    private readonly ICustomFormatService _formats = formats;
    private readonly ISeasonAvailabilityEvaluator _seasonEvaluator = seasonEvaluator;
    private readonly ILogger<FulfillmentQueue> _logger = logger;

    public async Task<bool> EnqueueAsync(MediaRequestDto request, bool force = false)
    {
        var active = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id &&
            j.Status != FulfillmentStatus.Completed &&
            j.Status != FulfillmentStatus.Failed &&
            j.Status != FulfillmentStatus.Cancelled);
        if (active) return false;

        // Other active jobs for the SAME title from a DIFFERENT request (two users, or one user
        // requesting different seasons at different times) — never let two jobs re-download the same
        // content concurrently. Subtracted from what this job would target, below.
        var otherActive = await _db.FulfillmentJobs.Where(j =>
                j.MediaId == request.MediaId && j.MediaType == request.MediaType &&
                j.MediaRequestId != request.Id &&
                j.Status != FulfillmentStatus.Completed && j.Status != FulfillmentStatus.Failed && j.Status != FulfillmentStatus.Cancelled)
            .ToListAsync();

        if (request.MediaType != MediaType.TvShow && otherActive.Count > 0)
            return false; // no sub-scope for movies/other media — one in-flight job for the title is enough

        var seasonsCsv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
        var episodesCsv = request.RequestedEpisodesCsv;
        List<SeasonTarget> seasonTargets = new();

        // Never re-download content already on Plex: narrow a TV request to only the missing seasons/
        // episodes. If nothing is missing, don't enqueue at all — the reconciliation service marks the
        // request Available. (Movies fall through unchanged.) A forced re-download skips this entirely.
        if (request.MediaType == MediaType.TvShow && !force)
        {
            var (s, e, targets, hasTarget) = await ComputeMissingTvTargetsAsync(request);
            if (!hasTarget) return false;   // everything already on Plex
            seasonsCsv = s;
            episodesCsv = e;
            seasonTargets = targets;

            if (otherActive.Count > 0)
            {
                if (otherActive.Any(j => ExpandJobScope(j).coversEverything))
                    return false; // another in-flight job already covers the whole show — nothing left to add

                var inFlightSeasons = new HashSet<int>();
                var inFlightEpisodes = new HashSet<(int season, int episode)>();
                foreach (var j in otherActive)
                {
                    var scope = ExpandJobScope(j);
                    switch (scope.scope)
                    {
                        case InFlightScope.Season:
                            inFlightSeasons.UnionWith(scope.seasons);
                            break;
                        case InFlightScope.Episode:
                            inFlightEpisodes.UnionWith(scope.episodes);
                            break;
                    }
                }

                if (!string.IsNullOrWhiteSpace(episodesCsv))
                {
                    var remaining = episodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Where(tok =>
                        {
                            var m = Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
                            if (!m.Success) return true;
                            var se = (int.Parse(m.Groups[1].Value), int.Parse(m.Groups[2].Value));
                            return !inFlightEpisodes.Contains(se) && !inFlightSeasons.Contains(se.Item1);
                        }).ToList();
                    if (remaining.Count == 0) return false;
                    episodesCsv = string.Join(",", remaining);
                }
                else if (!string.IsNullOrWhiteSpace(seasonsCsv))
                {
                    var remainingSeasons = seasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .Select(x => int.TryParse(x, out var n) ? n : -1)
                        .Where(n => n >= 0 && !inFlightSeasons.Contains(n))
                        .ToList();
                    if (remainingSeasons.Count == 0) return false;
                    seasonsCsv = string.Join(",", remainingSeasons);
                    seasonTargets = seasonTargets.Where(t => remainingSeasons.Contains(t.Season)).ToList();
                }
            }
        }

        // Resolve the IMDb id + year up front so the downloader (EZTV/YTS are keyed by IMDb; year
        // disambiguates free-text searches and gates the ranker against wrong-year matches) needs no
        // further metadata lookups of its own.
        string? imdbId = null;
        List<string>? genres = null;
        int? year = null;
        bool isAnime = false;
        try
        {
            var detail = await _metadata.GetDetailsAsync(request.MediaId, request.MediaType);
            imdbId = detail?.ImdbId;
            genres = detail?.Genres;
            year = detail?.Year;
            isAnime = AnimeClassifier.IsAnime(detail?.Genres, detail?.Languages, detail?.Countries);
            if (string.IsNullOrEmpty(imdbId)) imdbId = await _metadata.GetImdbIdAsync(request.MediaId, request.MediaType);
        }
        catch { /* best-effort; downloader can still try by title/year */ }

        // Resolve the quality profile: the requester's pick when they're entitled to it, else the first
        // matching assignment rule, else the default. Previously the requester's choice was discarded
        // outright and a single admin rule decided everything.
        var reqEntity = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == request.Id);
        var profileId = await _profiles.ResolveProfileIdAsync(
            request.MediaType, request.MediaId, genres,
            requesterChoiceId: reqEntity?.QualityProfileId,
            userId: request.RequestedByUserId == 0 ? null : request.RequestedByUserId);

        // Persist the resolution back onto the request so the UI, the upgrade scan and any later re-enqueue
        // all agree on which profile governs it.
        if (reqEntity is not null && reqEntity.QualityProfileId != profileId)
        {
            reqEntity.QualityProfileId = profileId;
            await _db.SaveChangesAsync();
        }

        // The job still carries a flat resolution floor: the downloader ranks against Quality until the
        // shared ranker lands, at which point it consumes the profile's full tier list instead.
        var resolvedQuality = await _profiles.GetCutoffQualityAsync(profileId);

        _db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = year,
            TmdbId = request.MediaId, // MediaId is the TMDb id for the default provider
            ImdbId = imdbId,
            ExternalId = request.ExternalId,           // music/other-source id (TODO: downloader music support)
            ExternalSource = request.ExternalSource,
            RequestedSeasonsCsv = seasonsCsv,
            RequestedEpisodesCsv = episodesCsv,
            SeasonTargetsJson = seasonTargets.Count > 0 ? JsonSerializer.Serialize(seasonTargets) : null,
            Quality = resolvedQuality,
            QualityProfileId = profileId,
            GenresCsv = genres is { Count: > 0 } ? string.Join(",", genres) : null,
            IsAnime = isAnime,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return true;
    }

    /// <summary>
    /// Given a TV request, returns the seasons/episodes CSVs to actually fetch (only what's missing
    /// from Plex) and whether there's anything to fetch at all. A season is considered satisfied when
    /// Plex already has at least its TMDB episode count.
    /// </summary>
    private async Task<(string? seasonsCsv, string? episodesCsv, List<SeasonTarget> seasonTargets, bool hasTarget)> ComputeMissingTvTargetsAsync(MediaRequestDto request)
    {
        // Episode-level request: keep only episodes not already on Plex. (Fan-out is precise via the CSV;
        // no SeasonTargets needed — the downloader uses RequestedEpisodes directly.)
        if (!string.IsNullOrWhiteSpace(request.RequestedEpisodesCsv))
        {
            var onPlex = await _seasonEvaluator.GetPlexEpisodesAsync(request.MediaId);
            var wanted = ParseEpisodes(request.RequestedEpisodesCsv);   // reuses the existing S/E parser
            var missing = wanted.Where(w => !(onPlex.TryGetValue(w.Season, out var set) && set.Contains(w.Episode))).ToList();
            if (missing.Count == 0) return (null, null, new(), false);
            return (null, string.Join(",", missing.Select(m => $"S{m.Season}E{m.Episode}")), new(), true);
        }

        // Per-season completeness (Plex episode count vs. TMDB's expected count), from the single shared
        // evaluator also used by PlexApiService.GetAvailableSeasonsAsync and the reconciliation service.
        var evaluation = await _seasonEvaluator.EvaluateAsync(request.MediaId);

        // Which seasons are in scope: an explicit list, else (whole series) every season TMDB knows.
        var scopeSeasons = evaluation.Values.AsEnumerable();
        if (request.RequestedSeasons.Count > 0)
            scopeSeasons = scopeSeasons.Where(s => request.RequestedSeasons.Contains(s.SeasonNumber));
        var seasonsList = scopeSeasons.ToList();

        // If we can't resolve the season list (metadata miss, or requested seasons TMDB doesn't know
        // about), don't guess — enqueue as originally asked. Without episode counts the downloader can't
        // fan out, so SeasonTargets stays empty (pack-only).
        if (seasonsList.Count == 0)
        {
            var csv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
            return (csv, null, new(), true);
        }

        var missingSeasons = new List<int>();
        var targets = new List<SeasonTarget>();
        foreach (var s in seasonsList)
        {
            // Only fetch seasons that have actually started airing; future seasons are the monitor's job,
            // so a whole-series request of a caught-up show doesn't spawn a doomed job for an unaired season.
            if (!s.Complete && s.Aired)
            {
                missingSeasons.Add(s.SeasonNumber);
                targets.Add(new SeasonTarget { Season = s.SeasonNumber, EpisodeCount = s.ExpectedCount, MissingEpisodes = s.MissingEpisodes });
            }
        }
        if (missingSeasons.Count == 0) return (null, null, new(), false);
        return (string.Join(",", missingSeasons), null, targets, true);
    }

    public async Task<List<FulfillmentJobDto>> ClaimNextAsync(string workerId, int max = 1)
    {
        if (max < 1) max = 1;
        var now0 = DateTime.UtcNow;
        // Claim Queued jobs whose retry backoff (if any) has elapsed. Deferred jobs aren't Queued, so they're
        // skipped here — the scheduler's MissingSearch job flips them back to Queued once they're due.
        var jobs = await _db.FulfillmentJobs
            .Where(j => j.Status == FulfillmentStatus.Queued && (j.NextRetryAt == null || j.NextRetryAt <= now0))
            .OrderBy(j => j.CreatedAt)
            .Take(max)
            .ToListAsync();

        var now = DateTime.UtcNow;
        foreach (var j in jobs)
        {
            j.Status = FulfillmentStatus.Claimed;
            j.ClaimedBy = workerId;
            j.ClaimedAt = now;
            j.LastUpdatedAt = now;
            j.Attempts++;
        }
        if (jobs.Count > 0) await _db.SaveChangesAsync();

        var dtos = jobs.Select(Map).ToList();
        await AttachRankingContextAsync(dtos, jobs);
        return dtos;
    }

    /// <summary>
    /// Attach the quality profile and tier catalog to each claimed job. Embedded in the payload rather than
    /// fetched separately by the downloader so a search can't be ranked against a profile that changed
    /// halfway through it, and so ranking needs no extra round-trip.
    /// </summary>
    private async Task AttachRankingContextAsync(List<FulfillmentJobDto> dtos, List<FulfillmentJobEntity> entities)
    {
        if (dtos.Count == 0) return;

        var definitions = await _db.QualityDefinitions.AsNoTracking().OrderBy(d => d.SortWeight)
            .Select(d => new QualityDefinitionDto
            {
                Id = d.Id, Name = d.Name, Resolution = d.Resolution, Source = d.Source, SortWeight = d.SortWeight
            }).ToListAsync();

        var profileIds = entities.Where(e => e.QualityProfileId.HasValue)
            .Select(e => e.QualityProfileId!.Value).Distinct().ToList();
        var profiles = new Dictionary<int, QualityProfileDto>();
        var formatsByProfile = new Dictionary<int, List<CustomFormatDto>>();
        foreach (var id in profileIds)
        {
            var dto = await _profiles.GetProfileAsync(id);
            if (dto is not null) profiles[id] = dto;
            // Scores are per profile, so each job carries the formats already priced for its own profile.
            formatsByProfile[id] = await _formats.GetForProfileAsync(id);
        }

        var now = DateTime.UtcNow;
        var requestIds = entities.Select(e => e.MediaRequestId).Distinct().ToList();
        var blocked = await _db.ReleaseBlocklist
            .Where(b => b.InfoHash != null && (b.ExpiresAt == null || b.ExpiresAt > now))
            .Where(b => (b.MediaRequestId != null && requestIds.Contains(b.MediaRequestId.Value))
                        || b.Scope != BlocklistScope.Request)
            .Select(b => new { b.MediaRequestId, b.Scope, b.MediaId, b.MediaType, Hash = b.InfoHash! })
            .ToListAsync();

        foreach (var (dto, entity) in dtos.Zip(entities))
        {
            dto.QualityDefinitions = definitions;
            if (entity.QualityProfileId is int pid && profiles.TryGetValue(pid, out var profile))
            {
                dto.QualityProfile = profile;
                if (formatsByProfile.TryGetValue(pid, out var formats))
                    dto.CustomFormats = formats.Where(f => f.Enabled).ToList();
            }

            dto.BlocklistedHashes = blocked
                .Where(b => b.MediaRequestId == entity.MediaRequestId
                            || (b.Scope == BlocklistScope.Media && b.MediaId == entity.MediaId && b.MediaType == entity.MediaType)
                            || b.Scope == BlocklistScope.Global)
                .Select(b => b.Hash).Distinct().ToList();
        }
    }

    public async Task<bool> ReportProgressAsync(int jobId, int progress)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return false;
        j.Status = FulfillmentStatus.Downloading;
        j.Progress = Math.Clamp(progress, 0, 100);
        j.LastUpdatedAt = DateTime.UtcNow;

        // Real bytes are moving, so this job found a release: clear the "couldn't find anything" state.
        // FulfillmentJobEntity documents DeferCount as "reset when a real download attempt begins" but
        // nothing ever did it, so the backoff ratcheted 15m -> 30m -> ... -> 24h and never came back, and
        // Escalated stuck on permanently. Gated on progress > 0 because the pipeline also reports 0 right
        // after claiming, before it has searched — that isn't evidence of anything yet.
        if (j.Progress > 0 && (j.DeferCount > 0 || j.Escalated || j.NextRetryAt is not null))
        {
            j.DeferCount = 0;
            j.Escalated = false;
            j.NextRetryAt = null;
        }

        // Move the request out of Searching. The reaper and the deferral endpoint both park requests there
        // and nothing used to move them back, which left them invisible to availability reconciliation.
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == j.MediaRequestId);
        if (req is not null && req.Status is RequestStatus.Searching or RequestStatus.Approved)
            req.Status = RequestStatus.Processing;

        await _db.SaveChangesAsync();
        return true;
    }

    public async Task MarkCompletedAsync(int mediaRequestId)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.Completed;
        j.Progress = 100;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task MarkFailedAsync(int mediaRequestId, string reason)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.Failed;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task MarkPartiallyCompletedAsync(int mediaRequestId, string reason)
    {
        var j = await LatestJobAsync(mediaRequestId);
        if (j is null) return;
        j.Status = FulfillmentStatus.PartiallyCompleted;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.CompletedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<DeferResult> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected = false)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return new DeferResult(false, false, 0, null, false);

        var now = DateTime.UtcNow;
        j.DeferCount++;
        // Only an empty-handed search against a real candidate pool counts toward relaxing the quality floor.
        if (candidatesRejected) j.EmptySearchCount++;
        j.NextRetryAt = RetryBackoff.ComputeNextRetry(j.DeferCount, j.ReleaseDate, now);
        j.Status = FulfillmentStatus.Deferred;
        j.LastError = reason.Length > 2000 ? reason[..2000] : reason;
        j.LastUpdatedAt = now;
        j.ClaimedBy = null;          // release the claim so it's not seen as an active download
        j.Progress = 0;

        // Escalate to admins exactly once, after enough empty searches — never auto-fail.
        bool shouldEscalate = !j.Escalated && j.DeferCount >= RetryBackoff.EscalateAfterDeferrals;
        if (shouldEscalate) j.Escalated = true;

        await _db.SaveChangesAsync();
        return new DeferResult(true, j.IsUpgrade, j.DeferCount, j.NextRetryAt, shouldEscalate);
    }

    public async Task MarkUpgradeExhaustedAsync(int jobId)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return;
        // The content is already available; an upgrade simply found nothing better. Close the job quietly
        // (not a failure) and stamp the request's cooldown so it's reconsidered on a later scan, not at once.
        j.Status = FulfillmentStatus.Cancelled;
        j.LastError = "No better-quality release found";
        j.CompletedAt = DateTime.UtcNow;
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == j.MediaRequestId);
        if (req is not null) req.LastUpgradeSearchAt = DateTime.UtcNow;
        await _db.SaveChangesAsync();
    }

    public async Task<bool> EnqueueUpgradeAsync(MediaRequestDto request, Quality target, IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes)
    {
        // Never run two upgrades for the same request at once.
        var activeUpgrade = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id && j.IsUpgrade &&
            j.Status != FulfillmentStatus.Completed && j.Status != FulfillmentStatus.Failed && j.Status != FulfillmentStatus.Cancelled);
        if (activeUpgrade) return false;

        // Copy identity fields (IMDb id / year / genres / anime flag) from the most recent real job so the
        // ranker can search without another metadata lookup; fall back to the request where needed.
        var origin = await _db.FulfillmentJobs
            .Where(j => j.MediaRequestId == request.Id && !j.IsUpgrade)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync();

        var episodesCsv = episodes.Count > 0
            ? string.Join(",", episodes.Select(e => $"S{e.season}E{e.episode}"))
            : null;

        _db.FulfillmentJobs.Add(new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            Title = request.Title,
            Year = origin?.Year,
            TmdbId = origin?.TmdbId ?? request.MediaId,
            ImdbId = origin?.ImdbId,
            TvdbId = origin?.TvdbId,
            ExternalId = request.ExternalId,
            ExternalSource = request.ExternalSource,
            RequestedEpisodesCsv = episodesCsv,
            Quality = target,
            GenresCsv = origin?.GenresCsv,
            IsAnime = origin?.IsAnime ?? false,
            IsUpgrade = true,
            ReplacePathsJson = replacePaths.Count > 0 ? JsonSerializer.Serialize(replacePaths) : null,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        });
        await _db.SaveChangesAsync();
        return true;
    }

    public async Task<Quality> RecomputeAchievedQualityAsync(int mediaRequestId)
    {
        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == mediaRequestId);
        if (req is null) return Quality.Any;

        // All video files currently imported for this request, across its jobs (superseded files are removed
        // when an upgrade replaces them, so what remains is the library's current state).
        var jobIds = await _db.FulfillmentJobs.Where(j => j.MediaRequestId == mediaRequestId)
            .Select(j => j.Id).ToListAsync();
        var heights = await _db.ImportedFiles
            .Where(f => jobIds.Contains(f.FulfillmentJobId) && f.FileType == "video" && f.ResolutionHeight > 0)
            .Select(f => f.ResolutionHeight)
            .ToListAsync();

        // Our own import records are the first source, but they are frequently blank: ResolutionHeight was
        // only populated from a later release, so everything imported before that reads as 0 and the request
        // ends up permanently Unknown — never claimed met, never upgraded, invisible. Eight requests on the
        // live deployment sat in exactly that state, including 720p episodes of a show with plentiful 1080p
        // releases and the "search for better versions" toggle switched on.
        //
        // Plex already knows. The library scan records the real resolution per episode, and the UI has been
        // displaying it the whole time ("720p H.264 · 1.6 GB"). So when our own records cannot answer, ask
        // the thing that can rather than giving up.
        if (heights.Count == 0)
        {
            var fromPlex = await PlexResolutionHeightsAsync(req);
            if (fromPlex.Count > 0)
            {
                heights = fromPlex;
                _logger.LogInformation(
                    "Request {RequestId} \"{Title}\": no resolution in our import records; Plex reports {Count} episode(s), worst {Worst}p",
                    req.Id, req.Title, fromPlex.Count, fromPlex.Min());
            }
        }

        // Achieved quality is the WORST video we hold (a single 720p episode leaves a 1080p season below cutoff).
        var achieved = heights.Count == 0 ? Quality.Any : QualityHelper.FromHeight(heights.Min());
        req.AchievedQuality = achieved;

        // Target: the cutoff of the request's profile, re-read each time so an edit to the profile re-flags
        // the cutoff correctly. Falls back to re-resolving (genres from the enqueue-time job snapshot) for a
        // request that predates profiles and hasn't been assigned one yet.
        int profileId = req.QualityProfileId ?? 0;
        if (profileId == 0)
        {
            var genresCsv = await _db.FulfillmentJobs.Where(j => j.MediaRequestId == mediaRequestId)
                .OrderByDescending(j => j.Id).Select(j => j.GenresCsv).FirstOrDefaultAsync();
            var genres = string.IsNullOrWhiteSpace(genresCsv) ? null
                : genresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            profileId = await _profiles.ResolveProfileIdAsync(req.MediaType, req.MediaId, genres,
                requesterChoiceId: null, userId: req.RequestedByUserId);
            req.QualityProfileId = profileId;
        }
        var target = await _profiles.GetCutoffQualityAsync(profileId);

        // Record the finest-grained tier we can for the worst file. Source isn't known here (it lives in the
        // release name, which the importer only started storing alongside these rows), so this lands on the
        // Unknown-source tier for that resolution — accurate rather than guessed.
        if (heights.Count > 0)
        {
            var worstTier = (int)achieved;
            req.AchievedQualityDefinitionId = await _db.QualityDefinitions
                .Where(d => d.Resolution == worstTier && d.Source == ReleaseSource.Unknown)
                .Select(d => (int?)d.Id).FirstOrDefaultAsync();
        }

        // No file with a parsed resolution means we can't judge — say so rather than claiming the cutoff is
        // met. The old boolean forced that case to "met", which silently made the request ineligible for an
        // upgrade forever, and unparsed resolutions are common (plenty of release names carry no 1080p token).
        req.CutoffState =
            heights.Count == 0 ? CutoffState.Unknown
            : target == Quality.Any ? CutoffState.Met
            : (int)achieved >= (int)target ? CutoffState.Met
            : CutoffState.Unmet;
        req.CutoffMet = req.CutoffState == CutoffState.Met; // legacy mirror, see MediaRequestEntity

        await _db.SaveChangesAsync();
        return achieved;
    }

    private Task<FulfillmentJobEntity?> LatestJobAsync(int mediaRequestId) =>
        _db.FulfillmentJobs
            .Where(x => x.MediaRequestId == mediaRequestId)
            .OrderByDescending(x => x.Id)
            .FirstOrDefaultAsync();

    // What another in-flight job is already targeting for the same show, for cross-request dedup:
    // explicit episodes/seasons from its CSVs, or — when a job was enqueued pack-only because metadata
    // was unavailable at the time (both CSVs null) — "covers everything", since we can't enumerate what
    // it actually targets and shouldn't risk a duplicate whole-series download.
    private enum InFlightScope
    {
        Episode,
        Season,
        WholeShow
    }

    private static (HashSet<int> seasons, HashSet<(int season, int episode)> episodes, bool coversEverything, InFlightScope scope) ExpandJobScope(FulfillmentJobEntity job)
    {
        var seasons = new HashSet<int>();
        var episodes = new HashSet<(int, int)>();
        if (!string.IsNullOrWhiteSpace(job.RequestedEpisodesCsv))
        {
            foreach (var tok in job.RequestedEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var m = Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
                if (!m.Success) continue;
                var s = int.Parse(m.Groups[1].Value);
                var e = int.Parse(m.Groups[2].Value);
                episodes.Add((s, e));
                seasons.Add(s);
            }
            return (seasons, episodes, false, InFlightScope.Episode);
        }
        if (!string.IsNullOrWhiteSpace(job.RequestedSeasonsCsv))
        {
            foreach (var tok in job.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                if (int.TryParse(tok, out var s)) seasons.Add(s);
            return (seasons, episodes, false, InFlightScope.Season);
        }
        return (seasons, episodes, true, InFlightScope.WholeShow);
    }

    // Parse "S1E1,S2E5" into episode targets.
    private static List<EpisodeRef> ParseEpisodes(string? csv)
    {
        var list = new List<EpisodeRef>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var tok in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(tok, @"^[Ss](\d+)[Ee](\d+)$");
            if (m.Success) list.Add(new EpisodeRef { Season = int.Parse(m.Groups[1].Value), Episode = int.Parse(m.Groups[2].Value) });
        }
        return list;
    }

    /// <summary>
    /// Read a job without touching its status. Deliberately NOT a claim: the reconciler imports torrents
    /// belonging to jobs that may already be Deferred or Failed — which is exactly the case that stranded
    /// nine finished downloads — so claiming or reviving the job here would fight the rest of the system.
    /// </summary>
    public async Task<FulfillmentJobDto?> GetJobAsync(int jobId)
    {
        var job = await _db.FulfillmentJobs.AsNoTracking().FirstOrDefaultAsync(j => j.Id == jobId);
        return job is null ? null : Map(job);
    }

    /// <summary>
    /// Per-episode resolutions Plex reports for this request's scope, as pixel heights.
    ///
    /// Scoped to the episodes the request actually covers: a request for S3E1 must not be judged by the
    /// quality of S1, or one bad episode elsewhere in the show would drag an unrelated request below cutoff
    /// forever. When the request names no specific episodes it covers the whole show, and every episode
    /// counts.
    /// </summary>
    private async Task<List<int>> PlexResolutionHeightsAsync(MediaRequestEntity req)
    {
        var byEpisode = await PlexEpisodeHeightsAsync(req.MediaType, req.MediaId, req.RequestedEpisodesCsv);
        return byEpisode.Values.ToList();
    }

    /// <summary>Public, per-episode form of the same lookup — the upgrade scan needs to know WHICH episodes
    /// are below target, not just whether any are, so it can scope the re-download instead of re-fetching the
    /// whole show.</summary>
    public Task<Dictionary<(int Season, int Episode), int>> GetPlexEpisodeHeightsAsync(MediaRequestDto request) =>
        PlexEpisodeHeightsAsync(request.MediaType, request.MediaId, request.RequestedEpisodesCsv);

    private async Task<Dictionary<(int Season, int Episode), int>> PlexEpisodeHeightsAsync(
        MediaType mediaType, int mediaId, string? requestedEpisodesCsv)
    {
        if (mediaType is not (MediaType.TvShow or MediaType.Anime)) return new();

        var ratingKey = await _seasonEvaluator.ResolveRatingKeyAsync(mediaId);
        if (string.IsNullOrEmpty(ratingKey)) return new();

        var seasons = await _db.PlexSeasonAvailability.AsNoTracking()
            .Where(s => s.ShowRatingKey == ratingKey && s.EpisodeQualityJson != null)
            .ToListAsync();
        if (seasons.Count == 0) return new();

        var wanted = ParseEpisodeScope(requestedEpisodesCsv);   // (season, episode) pairs, or empty for "everything"
        var heights = new Dictionary<(int, int), int>();

        foreach (var season in seasons)
        {
            Dictionary<string, PlexEpisodeQuality>? byEpisode;
            try
            {
                byEpisode = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, PlexEpisodeQuality>>(
                    season.EpisodeQualityJson!, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            }
            catch { continue; }   // a malformed blob must not take down the recompute
            if (byEpisode is null) continue;

            foreach (var (key, q) in byEpisode)
            {
                if (!int.TryParse(key, out var episode)) continue;
                if (wanted.Count > 0 && !wanted.Contains((season.SeasonNumber, episode))) continue;

                var h = HeightFromPlexLabel(q.Resolution);
                if (h > 0) heights[(season.SeasonNumber, episode)] = h;
            }
        }
        return heights;
    }

    /// <summary>The (season, episode) pairs a request covers. Empty means "the whole show".</summary>
    private static HashSet<(int Season, int Episode)> ParseEpisodeScope(string? requestedEpisodesCsv)
    {
        var set = new HashSet<(int, int)>();
        if (string.IsNullOrWhiteSpace(requestedEpisodesCsv)) return set;

        foreach (var token in requestedEpisodesCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var m = System.Text.RegularExpressions.Regex.Match(token, @"^[Ss](\d+)[Ee](\d+)$");
            if (m.Success && int.TryParse(m.Groups[1].Value, out var s) && int.TryParse(m.Groups[2].Value, out var e))
                set.Add((s, e));
        }
        return set;
    }

    /// <summary>Plex reports a label ("4K", "1080p", "SD"), not a number.</summary>
    private static int HeightFromPlexLabel(string? label) => (label ?? string.Empty).Trim().ToLowerInvariant() switch
    {
        "4k" or "2160p" => 2160,
        "1080p" => 1080,
        "720p" => 720,
        "480p" or "sd" => 480,
        _ => 0
    };

    private sealed class PlexEpisodeQuality
    {
        public string? Resolution { get; set; }
    }

    private static FulfillmentJobDto Map(FulfillmentJobEntity j) => new()
    {
        Id = j.Id,
        MediaRequestId = j.MediaRequestId,
        MediaId = j.MediaId,
        MediaType = j.MediaType,
        Title = j.Title,
        Year = j.Year,
        TmdbId = j.TmdbId,
        ImdbId = j.ImdbId,
        TvdbId = j.TvdbId,
        RequestedSeasons = string.IsNullOrWhiteSpace(j.RequestedSeasonsCsv)
            ? new List<int>()
            : j.RequestedSeasonsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var n) ? n : (int?)null)
                .Where(n => n.HasValue).Select(n => n!.Value).ToList(),
        RequestedEpisodes = ParseEpisodes(j.RequestedEpisodesCsv),
        SeasonTargets = string.IsNullOrWhiteSpace(j.SeasonTargetsJson)
            ? new List<SeasonTarget>()
            : (JsonSerializer.Deserialize<List<SeasonTarget>>(j.SeasonTargetsJson) ?? new List<SeasonTarget>()),
        Quality = j.Quality,
        EmptySearchCount = j.EmptySearchCount,
        IsManualGrab = j.IsManualGrab,
        ForcedMagnet = j.ForcedMagnet,
        ForcedReleaseName = j.ForcedReleaseName,
        ForcedIndexerId = j.ForcedIndexerId,
        Genres = string.IsNullOrWhiteSpace(j.GenresCsv)
            ? new List<string>()
            : j.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IsAnime = j.IsAnime,
        Status = j.Status,
        Attempts = j.Attempts,
        Progress = j.Progress,
        IsUpgrade = j.IsUpgrade,
        ReplacePaths = string.IsNullOrWhiteSpace(j.ReplacePathsJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(j.ReplacePathsJson) ?? new List<string>())
    };
}
