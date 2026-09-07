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
using PlexRequestsHosted.Shared.Media;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Database-backed <see cref="IFulfillmentQueue"/>. SQLite is single-writer, so claims are safe
/// without extra locking at our scale; swap for Redis/RabbitMQ later without touching callers.
/// </summary>
public class FulfillmentQueue(AppDbContext db, IMediaMetadataProvider metadata,
    IMusicDirectAcquisitionResolver directMusic, IQualityProfileService profiles,
    ICustomFormatService formats, ISeasonAvailabilityEvaluator seasonEvaluator,
    ILibraryOrganizationPreferencesService libraryPreferences,
    ILogger<FulfillmentQueue> logger,
    ITmdbEpisodeGroupImportService? episodeGroups = null) : IFulfillmentQueue
{
    private readonly AppDbContext _db = db;
    private readonly IMediaMetadataProvider _metadata = metadata;
    private readonly IMusicDirectAcquisitionResolver _directMusic = directMusic;
    private readonly IQualityProfileService _profiles = profiles;
    private readonly ICustomFormatService _formats = formats;
    private readonly ISeasonAvailabilityEvaluator _seasonEvaluator = seasonEvaluator;
    private readonly ILibraryOrganizationPreferencesService _libraryPreferences = libraryPreferences;
    private readonly ILogger<FulfillmentQueue> _logger = logger;
    private readonly ITmdbEpisodeGroupImportService? _episodeGroups = episodeGroups;

    public async Task<bool> EnqueueAsync(MediaRequestDto request, bool force = false)
    {
        var reqEntity = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == request.Id);
        var mediaRef = request.MediaRef is { IsValid: true } suppliedRef
            ? suppliedRef
            : !string.IsNullOrWhiteSpace(request.ExternalId)
                ? MediaRef.FromExternal(request.ExternalSource ?? "external", request.ExternalId,
                    request.MediaType, request.RequestScopeKind.ToMediaKind(request.MediaType))
                : MediaRef.FromTmdb(request.MediaId, request.MediaType);

        var active = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id &&
            (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
             j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred));
        if (active) return false;

        // Other active jobs for the SAME title from a DIFFERENT request (two users, or one user
        // requesting different seasons at different times) — never let two jobs re-download the same
        // content concurrently. Subtracted from what this job would target, below.
        var otherActiveQuery = _db.FulfillmentJobs.Where(j =>
            j.MediaRequestId != request.Id &&
            (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
             j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred));
        otherActiveQuery = reqEntity?.MediaIdentityId is int identityId
            ? otherActiveQuery.Where(j => j.MediaIdentityId == identityId)
            : otherActiveQuery.Where(j => j.MediaIdentityId == null && j.MediaId == request.MediaId
                                                                  && j.MediaType == request.MediaType);
        var otherActive = await otherActiveQuery.ToListAsync();

        if (request.MediaType is not (MediaType.TvShow or MediaType.Anime) && otherActive.Count > 0)
            return false; // no sub-scope for movies/other media — one in-flight job for the title is enough

        var seasonsCsv = request.RequestedSeasons.Count > 0 ? string.Join(",", request.RequestedSeasons) : null;
        var episodesCsv = request.RequestedEpisodesCsv;
        List<SeasonTarget> seasonTargets = new();

        // Never re-download content already on Plex: narrow a TV request to only the missing seasons/
        // episodes. If nothing is missing, don't enqueue at all — the reconciliation service marks the
        // request Available. (Movies fall through unchanged.) A forced re-download skips this entirely.
        if (request.MediaType is MediaType.TvShow or MediaType.Anime && !force)
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
        List<CanonicalSeasonIdentityDto> canonicalSeasons = new();
        bool isAnime = request.IsAnime == true || reqEntity?.IsAnime == true || request.MediaType == MediaType.Anime;
        MusicAcquisitionContextDto? music = null;
        try
        {
            var detail = await _metadata.GetDetailsAsync(mediaRef);
            imdbId = detail?.ImdbId;
            genres = detail?.Genres;
            year = detail?.Year;
            if (request.MediaType is MediaType.TvShow or MediaType.Anime)
                canonicalSeasons = (detail?.Seasons ?? []).Where(season => season.SeasonNumber > 0)
                    .Select(season => new CanonicalSeasonIdentityDto
                    {
                        Season = season.SeasonNumber,
                        Name = season.Name,
                        EpisodeCount = season.EpisodeCount
                    }).ToList();
            isAnime = isAnime || AnimeClassifier.IsAnime(detail?.Genres, detail?.Languages, detail?.Countries);
            if (request.MediaType == MediaType.Music)
            {
                music = BuildMusicContext(mediaRef.Kind, request.Title, detail);
                if (detail is not null && await DirectMusicEnabledAsync())
                    music.DirectAudio = await _directMusic.ResolveAsync(mediaRef, detail);
            }
            else if (string.IsNullOrEmpty(imdbId))
                imdbId = await _metadata.GetImdbIdAsync(request.MediaId, request.MediaType);
        }
        catch { /* best-effort; downloader can still try by title/year */ }

        // A metadata outage must not erase the identity needed by future retries. The fallback still gives
        // the ranker the correct request shape and title; it simply has less artist context for this pass.
        if (request.MediaType == MediaType.Music)
            music ??= BuildMusicContext(mediaRef.Kind, request.Title, null);

        // Resolve the quality profile: the requester's pick when they're entitled to it, else the first
        // matching assignment rule, else the default. Previously the requester's choice was discarded
        // outright and a single admin rule decided everything.
        var profileId = await _profiles.ResolveProfileIdAsync(
            request.MediaType, request.MediaId, genres,
            requesterChoiceId: reqEntity?.QualityProfileId,
            userId: request.RequestedByUserId == 0 ? null : request.RequestedByUserId,
            isAnime: isAnime);

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
        var languagePolicy = MediaLanguagePolicy.FromProfile(await _profiles.GetProfileAsync(profileId));

        // Resolve exactly once at the enqueue/approval boundary. The snapshot below remains authoritative
        // even if an admin later reorders rules, edits a root, or disables the destination mid-download.
        var librarySettings = await _libraryPreferences.GetAsync();
        var libraryDestination = LibraryRouting.Resolve(
            librarySettings, request.MediaType, resolvedQuality, genres, isAnime,
            isEpisode: mediaRef.Kind == MediaKind.Series,
            preferredDestinationId: reqEntity?.LibraryDestinationId ?? request.LibraryDestinationId);
        if (reqEntity is not null && !string.Equals(reqEntity.LibraryDestinationId, libraryDestination.Id,
                StringComparison.OrdinalIgnoreCase))
        {
            reqEntity.LibraryDestinationId = libraryDestination.Id;
            await _db.SaveChangesAsync();
        }

        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaIdentityId = reqEntity?.MediaIdentityId,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            MediaKind = mediaRef.Kind,
            RequestScopeKind = request.RequestScopeKind,
            AcquisitionContextJson = music is null ? null : JsonSerializer.Serialize(music),
            Title = request.Title,
            Year = year,
            TmdbId = mediaRef.TryGetTmdbId(out var tmdbId) ? tmdbId : null,
            ImdbId = imdbId,
            ExternalId = mediaRef.Provider == "tmdb" ? null : mediaRef.Id,
            ExternalSource = mediaRef.Provider == "tmdb" ? null : mediaRef.Provider,
            RequestedSeasonsCsv = seasonsCsv,
            RequestedEpisodesCsv = episodesCsv,
            SeasonTargetsJson = seasonTargets.Count > 0 ? JsonSerializer.Serialize(seasonTargets) : null,
            CanonicalSeasonsJson = canonicalSeasons.Count > 0 ? JsonSerializer.Serialize(canonicalSeasons) : null,
            Quality = resolvedQuality,
            QualityProfileId = profileId,
            MediaLanguagePolicyJson = MediaLanguagePolicy.IsActive(languagePolicy)
                ? JsonSerializer.Serialize(languagePolicy)
                : null,
            EpisodeOrderProfileJson = EpisodeOrderMapping.Resolve(librarySettings.SeriesEpisodeOrderProfiles,
                    mediaRef.TryGetTmdbId(out var profileTmdbId) ? profileTmdbId : null) is { } episodeProfile
                ? JsonSerializer.Serialize(episodeProfile)
                : null,
            GenresCsv = genres is { Count: > 0 } ? string.Join(",", genres) : null,
            IsAnime = isAnime,
            LibraryDestinationId = libraryDestination.Id,
            LibraryDestinationName = libraryDestination.Name,
            LibraryDestinationKind = libraryDestination.ContentKind,
            LibraryDestinationRootPath = libraryDestination.RootPath,
            LibraryPlexSectionId = libraryDestination.PlexSectionId,
            LibraryDestinationTemplate = libraryDestination.Template,
            LibrarySeasonPackFolderTemplate = libraryDestination.SeasonPackFolderTemplate,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };
        _db.FulfillmentJobs.Add(job);
        try
        {
            await _db.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            // A second producer may have won the race after the active check. The filtered unique index is
            // the final arbiter; detach our losing insert and report the normal idempotent no-op outcome.
            _db.Entry(job).State = EntityState.Detached;
            if (await _db.FulfillmentJobs.AnyAsync(j => j.MediaRequestId == request.Id &&
                    (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
                     j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred)))
                return false;
            throw;
        }
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
                targets.Add(new SeasonTarget
                {
                    Season = s.SeasonNumber,
                    Name = s.Name,
                    EpisodeCount = s.ExpectedCount,
                    MissingEpisodes = s.MissingEpisodes
                });
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

        // Older queued jobs predate immutable media-language snapshots. Hydrate that contract before the
        // claim crosses the process boundary; otherwise the worker cannot enforce track languages or fix
        // playback defaults and a retry can import a foreign-first release under today's Smart profile.
        await SnapshotMissingMediaLanguagePoliciesAsync(jobs);
        await SnapshotMissingEpisodeOrderProfilesAsync(jobs);
        await SnapshotMissingSeasonTargetNamesAsync(jobs);
        var now = DateTime.UtcNow;
        foreach (var j in jobs)
        {
            if (j.MediaType == MediaType.Music)
                await RefreshMusicContextAsync(j);
            j.Status = FulfillmentStatus.Claimed;
            j.ClaimedBy = workerId;
            j.ClaimedAt = now;
            j.LastUpdatedAt = now;
            j.Attempts++;
        }
        if (jobs.Count > 0) await _db.SaveChangesAsync();

        var dtos = jobs.Select(Map).ToList();
        await AttachRankingContextAsync(dtos, jobs);
        await AttachEpisodeOrderCandidatesAsync(dtos);
        return dtos;
    }

    /// <summary>Offer official episode-group snapshots to the downloader only for anime jobs that have an
    /// exact target set and no configured order. Discovery is bounded and best-effort: TMDb downtime must
    /// never make claiming the durable queue fail.</summary>
    private async Task AttachEpisodeOrderCandidatesAsync(IReadOnlyList<FulfillmentJobDto> jobs)
    {
        if (_episodeGroups is null) return;
        var eligible = jobs.Where(job => job.IsAnime
                                         && job.MediaType is MediaType.TvShow or MediaType.Anime
                                         && job.EpisodeOrderProfile is null
                                         && job.TmdbId is > 0
                                         && (job.RequestedEpisodes.Count > 0
                                             || job.SeasonTargets.Any(target => target.MissingEpisodes.Count > 0)))
            .ToList();
        if (eligible.Count == 0) return;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
        var bySeries = new Dictionary<int, List<SeriesEpisodeOrderProfileDto>>();
        foreach (var job in eligible)
        {
            var tmdbId = job.TmdbId!.Value;
            if (!bySeries.TryGetValue(tmdbId, out var candidates))
            {
                try
                {
                    candidates = await _episodeGroups.GetCandidateProfilesAsync(tmdbId, job.Title, timeout.Token);
                    bySeries[tmdbId] = candidates;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "TMDb episode-order candidates unavailable for anime job {JobId} ({Title})",
                        job.Id, job.Title);
                    candidates = [];
                    bySeries[tmdbId] = candidates;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogWarning("TMDb episode-order discovery timed out while claiming anime job {JobId} ({Title})",
                        job.Id, job.Title);
                    break;
                }
            }

            job.EpisodeOrderCandidates = candidates;
        }
    }

    /// <summary>Backfill canonical season names on old queued jobs without changing their episode scope.
    /// Names are immutable ranking evidence: they let anime releases such as uploader S04 "Second Season"
    /// map to canonical S03, while a bare number remains only a number.</summary>
    private async Task SnapshotMissingSeasonTargetNamesAsync(IReadOnlyList<FulfillmentJobEntity> jobs)
    {
        var candidates = jobs.Where(job => job.MediaType is MediaType.TvShow or MediaType.Anime)
            .ToList();
        foreach (var job in candidates)
        {
            List<SeasonTarget> targets = [];
            List<CanonicalSeasonIdentityDto> identities = [];
            try
            {
                if (!string.IsNullOrWhiteSpace(job.SeasonTargetsJson))
                    targets = JsonSerializer.Deserialize<List<SeasonTarget>>(job.SeasonTargetsJson) ?? [];
                if (!string.IsNullOrWhiteSpace(job.CanonicalSeasonsJson))
                    identities = JsonSerializer.Deserialize<List<CanonicalSeasonIdentityDto>>(job.CanonicalSeasonsJson) ?? [];
            }
            catch (JsonException ex)
            {
                _logger.LogWarning(ex, "Job {JobId}: cannot hydrate malformed canonical season identity", job.Id);
                continue;
            }
            if (identities.Count > 0 && targets.All(target => !string.IsNullOrWhiteSpace(target.Name))) continue;

            try
            {
                var tmdbId = job.TmdbId ?? (string.IsNullOrWhiteSpace(job.ExternalId) ? job.MediaId : 0);
                if (tmdbId <= 0) continue;
                var detail = await _metadata.GetDetailsAsync(tmdbId, MediaType.TvShow);
                var names = (detail?.Seasons ?? []).Where(season => season.SeasonNumber >= 0)
                    .ToDictionary(season => season.SeasonNumber, season => season.Name);
                var canonical = (detail?.Seasons ?? []).Where(season => season.SeasonNumber > 0)
                    .Select(season => new CanonicalSeasonIdentityDto
                    {
                        Season = season.SeasonNumber,
                        Name = season.Name,
                        EpisodeCount = season.EpisodeCount
                    }).ToList();
                var targetChanged = false;
                foreach (var target in targets.Where(target => string.IsNullOrWhiteSpace(target.Name)))
                {
                    if (!names.TryGetValue(target.Season, out var name) || string.IsNullOrWhiteSpace(name)) continue;
                    target.Name = name.Trim();
                    targetChanged = true;
                }
                if (identities.Count == 0 && canonical.Count > 0)
                    job.CanonicalSeasonsJson = JsonSerializer.Serialize(canonical);
                if (targetChanged) job.SeasonTargetsJson = JsonSerializer.Serialize(targets);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Job {JobId}: canonical season names could not be hydrated", job.Id);
            }
        }
    }

    public async Task<SeriesEpisodeOrderProfileDto?> ApplyEpisodeOrderGroupAsync(int jobId,
        string episodeGroupId, CancellationToken ct = default)
    {
        if (_episodeGroups is null || string.IsNullOrWhiteSpace(episodeGroupId)
            || episodeGroupId.Trim().Length > 128) return null;
        var job = await _db.FulfillmentJobs.FirstOrDefaultAsync(entity => entity.Id == jobId, ct);
        if (job is null || !job.IsAnime || job.MediaType is not (MediaType.TvShow or MediaType.Anime)
            || job.Status is FulfillmentStatus.Completed or FulfillmentStatus.Cancelled or FulfillmentStatus.Failed)
            return null;
        var tmdbId = job.TmdbId ?? (string.IsNullOrWhiteSpace(job.ExternalId) ? job.MediaId : null);
        if (tmdbId is not > 0) return null;

        if (!string.IsNullOrWhiteSpace(job.EpisodeOrderProfileJson))
        {
            try
            {
                var frozen = JsonSerializer.Deserialize<SeriesEpisodeOrderProfileDto>(job.EpisodeOrderProfileJson);
                return frozen is not null
                       && string.Equals(frozen.SourceEpisodeGroupId, episodeGroupId.Trim(), StringComparison.Ordinal)
                       && EpisodeOrderMapping.IsActive(frozen)
                       && EpisodeOrderMapping.TryParse(frozen, out _, out _)
                    ? frozen
                    : null;
            }
            catch (JsonException)
            {
                // A malformed old snapshot must not be overwritten automatically; the admin can replace it
                // explicitly from Library settings where the change is visible and confirmed.
                return null;
            }
        }

        EpisodeGroupImportPreviewDto preview;
        try
        {
            preview = await _episodeGroups.BuildPreviewAsync(tmdbId.Value, job.Title,
                episodeGroupId.Trim(), ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Rejected episode-group selection {GroupId} for anime job {JobId}",
                episodeGroupId, jobId);
            return null;
        }

        var profile = preview.Profile;
        if (!EpisodeOrderMapping.IsActive(profile)
            || !EpisodeOrderMapping.TryParse(profile, out _, out _)
            || profile.TmdbId != tmdbId.Value
            || !string.Equals(profile.SourceEpisodeGroupId, episodeGroupId.Trim(), StringComparison.Ordinal))
            return null;

        job.EpisodeOrderProfileJson = JsonSerializer.Serialize(profile);
        if (await _db.SaveChangesAsync(ct) == 0) return null;

        _logger.LogInformation(
            "Persisted uniquely matched TMDb episode group {GroupId} ({GroupName}) for anime job {JobId} only",
            profile.SourceEpisodeGroupId, profile.SourceEpisodeGroupName, jobId);
        return profile;
    }

    /// <summary>
    /// Jobs created before episode-order profiles existed have no immutable snapshot. Resolve that missing
    /// context at the last safe boundary, immediately before the job's first/new claim. Existing snapshots
    /// remain authoritative, including when an administrator later edits or removes the global profile.
    /// </summary>
    private async Task SnapshotMissingEpisodeOrderProfilesAsync(IReadOnlyList<FulfillmentJobEntity> jobs)
    {
        var legacySeries = jobs.Where(j => j.MediaType is MediaType.TvShow or MediaType.Anime
                                           && string.IsNullOrWhiteSpace(j.EpisodeOrderProfileJson))
            .ToList();
        if (legacySeries.Count == 0) return;

        var settings = await _libraryPreferences.GetAsync();
        var hydrated = 0;
        foreach (var job in legacySeries)
        {
            // Before provider-neutral identities, MediaId was the TMDb id. External identities must not be
            // mistaken for TMDb ids simply because their legacy numeric mirror happens to collide.
            var tmdbId = job.TmdbId ?? (string.IsNullOrWhiteSpace(job.ExternalId) ? job.MediaId : null);
            if (EpisodeOrderMapping.Resolve(settings.SeriesEpisodeOrderProfiles, tmdbId) is not { } profile)
                continue;
            job.EpisodeOrderProfileJson = JsonSerializer.Serialize(profile);
            hydrated++;
        }
        if (hydrated > 0)
            _logger.LogInformation("Snapshotted episode-order profiles onto {Count} legacy queued job(s)", hydrated);
    }

    /// <summary>
    /// Jobs created before media-track validation carried only a nullable profile id. Resolve their missing
    /// policy exactly once at the final safe boundary before claim. A non-empty snapshot is never replaced,
    /// so later profile edits cannot silently change an in-flight job. If no historical/default profile can
    /// be resolved, video jobs receive the installation's conservative Smart contract: preferred audio, or
    /// Japanese audio with preferred-language subtitles for anime.
    /// </summary>
    private async Task SnapshotMissingMediaLanguagePoliciesAsync(IReadOnlyList<FulfillmentJobEntity> jobs)
    {
        var legacyVideo = jobs.Where(job => job.MediaType is MediaType.Movie or MediaType.TvShow or MediaType.Anime
                                            && string.IsNullOrWhiteSpace(job.MediaLanguagePolicyJson))
            .ToList();
        if (legacyVideo.Count == 0) return;

        var requestIds = legacyVideo.Select(job => job.MediaRequestId).Distinct().ToList();
        var requests = await _db.MediaRequests.AsNoTracking()
            .Where(request => requestIds.Contains(request.Id))
            .Select(request => new
            {
                request.Id,
                request.QualityProfileId,
                request.RequestedByUserId
            })
            .ToDictionaryAsync(request => request.Id);
        var profiles = new Dictionary<int, QualityProfileDto?>();
        var hydrated = 0;
        var fallback = 0;

        foreach (var job in legacyVideo)
        {
            requests.TryGetValue(job.MediaRequestId, out var request);
            var profileId = job.QualityProfileId ?? request?.QualityProfileId;
            if (profileId is not > 0)
            {
                profileId = await _profiles.ResolveProfileIdAsync(job.MediaType, job.MediaId,
                    string.IsNullOrWhiteSpace(job.GenresCsv)
                        ? null
                        : job.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                    requesterChoiceId: null,
                    userId: request?.RequestedByUserId is > 0 ? request.RequestedByUserId : null,
                    isAnime: job.IsAnime);
                if (profileId is > 0) job.QualityProfileId = profileId;
            }

            QualityProfileDto? profile = null;
            if (profileId is > 0)
            {
                if (!profiles.TryGetValue(profileId.Value, out profile))
                {
                    profile = await _profiles.GetProfileAsync(profileId.Value);
                    profiles[profileId.Value] = profile;
                }
            }

            var policy = profile is null
                ? MediaLanguagePolicy.SmartDefault()
                : MediaLanguagePolicy.FromProfile(profile);
            if (!MediaLanguagePolicy.IsActive(policy)) continue;

            job.MediaLanguagePolicyJson = JsonSerializer.Serialize(policy);
            hydrated++;
            if (profile is null) fallback++;
        }

        if (hydrated > 0)
            _logger.LogInformation(
                "Snapshotted media-language policies onto {Count} legacy queued video job(s) ({Fallback} used the safe Smart default)",
                hydrated, fallback);
    }

    /// <summary>
    /// Refresh incomplete music identity on a retry. Metadata was best-effort at enqueue, and persisting a missing
    /// artist forever would make an album title ambiguous forever. Complete contexts return without I/O;
    /// a transient outage simply leaves the ranker rejecting with MetadataIncomplete,
    /// after which the existing durable deferral/backoff brings it through here again.
    /// </summary>
    private async Task RefreshMusicContextAsync(FulfillmentJobEntity job)
    {
        if (string.IsNullOrWhiteSpace(job.ExternalId)) return;
        MusicAcquisitionContextDto? current = null;
        try
        {
            current = string.IsNullOrWhiteSpace(job.AcquisitionContextJson)
                ? null : JsonSerializer.Deserialize<MusicAcquisitionContextDto>(job.AcquisitionContextJson);
        }
        catch (JsonException) { /* replace malformed transitional context from authoritative metadata */ }

        // Request scope is the durable fulfillment intent. Repair stale transitional MediaKind/context
        // combinations from it instead of allowing a complete contract for the wrong scope to survive.
        var kind = job.RequestScopeKind.ToMediaKind(MediaType.Music);
        var canonicalComplete = current?.SchemaVersion >= 2
            && current.Kind == kind && current.HasCompletionContract;
        var needsDirect = kind is MediaKind.Album or MediaKind.Track
            && current?.DirectAudio is null && await DirectMusicEnabledAsync();
        if (canonicalComplete && !needsDirect) return;

        try
        {
            var mediaRef = MediaRef.FromExternal(
                job.ExternalSource ?? "external", job.ExternalId, MediaType.Music, kind);
            var detail = await _metadata.GetDetailsAsync(mediaRef);
            if (detail is null) return;
            var refreshed = canonicalComplete ? current! : BuildMusicContext(kind, job.Title, detail);
            if (needsDirect)
                refreshed.DirectAudio = await _directMusic.ResolveAsync(mediaRef, detail);
            job.AcquisitionContextJson = JsonSerializer.Serialize(refreshed);
            job.Year = detail.Year;
            job.LastUpdatedAt = DateTime.UtcNow;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Music metadata refresh deferred for fulfillment job {JobId}", job.Id);
        }
    }

    private Task<bool> DirectMusicEnabledAsync() => _db.MusicSettings.AsNoTracking()
        .Where(x => x.IsSingleton).Select(x => x.DirectDownloadsEnabled).FirstOrDefaultAsync();

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
        var musicDirectEnabled = await _db.MusicSettings.AsNoTracking()
            .Where(x => x.IsSingleton)
            .Select(x => x.DirectDownloadsEnabled)
            .FirstOrDefaultAsync();
        var requestIds = entities.Select(e => e.MediaRequestId).Distinct().ToList();
        var blocked = await _db.ReleaseBlocklist
            .Where(b => (b.SourceId != null || b.InfoHash != null) && (b.ExpiresAt == null || b.ExpiresAt > now))
            .Where(b => (b.MediaRequestId != null && requestIds.Contains(b.MediaRequestId.Value))
                        || b.Scope != BlocklistScope.Request)
            .Select(b => new { b.MediaRequestId, b.Scope, b.MediaId, b.MediaType,
                b.Protocol, b.SourceId, b.InfoHash })
            .ToListAsync();

        foreach (var (dto, entity) in dtos.Zip(entities))
        {
            if (entity.MediaType == MediaType.Music && musicDirectEnabled)
                dto.AllowedAcquisitionProtocols.Add(AcquisitionProtocol.DirectAudio);
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
                .SelectMany(b => new[]
                {
                    b.InfoHash,
                    b.SourceId is null ? null : AcquisitionResource.BlocklistKey(b.Protocol, b.SourceId)
                }).Where(x => x is not null).Select(x => x!).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
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

        var now = DateTime.UtcNow;
        var importedFiles = await _db.ImportedFiles.AsNoTracking().Include(f => f.EpisodeCoverage)
            .Where(f => f.FulfillmentJobId == j.Id && f.FileType == "video")
            .ToListAsync();
        var imported = importedFiles.SelectMany(AuditCoverage).ToHashSet();
        var hasExplicitTargets = false;
        var remainingTargetCount = 0;
        var remainingSeasons = new List<int>();

        if (!string.IsNullOrWhiteSpace(j.RequestedEpisodesCsv))
        {
            var requested = ParseEpisodes(j.RequestedEpisodesCsv)
                .Select(x => (x.Season, x.Episode)).Distinct().ToList();
            hasExplicitTargets = requested.Count > 0;
            var remaining = requested.Where(x => !imported.Contains(x)).ToList();
            remainingTargetCount = remaining.Count;
            remainingSeasons = remaining.Select(x => x.Season).Distinct().Order().ToList();
            if (remaining.Count > 0 && remaining.Count < requested.Count)
            {
                j.RequestedEpisodesCsv = string.Join(",", remaining
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode)
                    .Select(x => $"S{x.Season}E{x.Episode}"));
            }
        }
        else if (!string.IsNullOrWhiteSpace(j.SeasonTargetsJson))
        {
            try
            {
                var targets = JsonSerializer.Deserialize<List<SeasonTarget>>(j.SeasonTargetsJson) ?? [];
                hasExplicitTargets = targets.Count > 0;
                var remaining = targets
                    .Select(target => new SeasonTarget
                    {
                        Season = target.Season,
                        Name = target.Name,
                        EpisodeCount = target.EpisodeCount,
                        MissingEpisodes = target.MissingEpisodes.Distinct().Order()
                            .Where(episode => !imported.Contains((target.Season, episode))).ToList()
                    })
                    .Where(target => target.MissingEpisodes.Count > 0)
                    .OrderBy(target => target.Season)
                    .ToList();
                remainingTargetCount = remaining.Sum(target => target.MissingEpisodes.Count);
                remainingSeasons = remaining.Select(target => target.Season).ToList();
                if (remaining.Count > 0 && remainingTargetCount < targets.Sum(x => x.MissingEpisodes.Distinct().Count()))
                {
                    j.SeasonTargetsJson = JsonSerializer.Serialize(remaining);
                    j.RequestedSeasonsCsv = string.Join(",", remainingSeasons);
                }
            }
            catch (JsonException ex)
            {
                // A malformed legacy target contract must never be replaced with an inferred/wider scope.
                // Keep the original job target and let the normal retry/escalation path expose it to admins.
                _logger.LogWarning(ex, "Job {JobId}: could not narrow malformed season targets after partial import", j.Id);
            }
        }

        var req = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == mediaRequestId);
        if (hasExplicitTargets && remainingTargetCount == 0)
        {
            // Earlier partial attempts plus this one can collectively complete the immutable target even
            // when the current planner record covered only a subset. Do not manufacture another search.
            j.Status = FulfillmentStatus.Completed;
            j.Progress = 100;
            j.CompletedAt = now;
            j.NextRetryAt = null;
            j.ClaimedBy = null;
            j.LastError = null;
            if (req is not null)
            {
                req.Status = RequestStatus.Available;
                req.AvailableAt = now;
                req.DenialReason = null;
            }
        }
        else
        {
            // A partial pack is useful progress, not an empty search. Queue the same immutable job for a
            // fresh plan after a short hand-off window, with only audit-proven missing targets remaining.
            // Keeping this one job also lets the durable import audit prevent duplicate episode downloads.
            j.Status = FulfillmentStatus.Queued;
            j.Progress = 0;
            j.CompletedAt = null;
            j.ClaimedBy = null;
            j.ClaimedAt = null;
            j.NextRetryAt = now.AddMinutes(1);
            j.LastUpdatedAt = now;
            j.IsManualGrab = false;
            j.ForcedMagnet = null;
            j.ForcedReleaseName = null;
            j.ForcedIndexerId = null;
            var continuation = remainingTargetCount > 0
                ? $" Continuing automatically with {remainingTargetCount} missing episode(s) across season(s) {string.Join(", ", remainingSeasons)}."
                : " Continuing automatically with the original scope because exact remaining episodes could not be proven.";
            var detail = reason + continuation;
            j.LastError = detail.Length > 2000 ? detail[..2000] : detail;
            if (req is not null)
            {
                req.Status = RequestStatus.PartiallyAvailable;
                req.DenialReason = reason.Length > 1000 ? reason[..1000] : reason;
                req.AvailableAt ??= now;
            }
            _logger.LogInformation(
                "Job {JobId}: partial import retained {ImportedCount} covered episode(s); queued continuation for {RemainingCount} remaining target(s)",
                j.Id, imported.Count, remainingTargetCount);
        }
        await _db.SaveChangesAsync();
    }

    public async Task<DeferResult> MarkDeferredAsync(int jobId, string reason, bool candidatesRejected = false)
    {
        var j = await _db.FulfillmentJobs.FirstOrDefaultAsync(x => x.Id == jobId);
        if (j is null) return new DeferResult(false, false, 0, null, false);

        var now = DateTime.UtcNow;
        if (j.IsReplacement && !string.IsNullOrWhiteSpace(j.RequestedEpisodesCsv))
        {
            var requested = ParseEpisodes(j.RequestedEpisodesCsv)
                .Select(x => (x.Season, x.Episode)).Distinct().ToList();
            var importedFiles = await _db.ImportedFiles.AsNoTracking().Include(f => f.EpisodeCoverage)
                .Where(f => f.FulfillmentJobId == j.Id && f.FileType == "video")
                .ToListAsync();
            var imported = importedFiles.SelectMany(AuditCoverage).ToHashSet();
            var remaining = requested.Where(x => !imported.Contains(x)).ToList();
            // Never turn an empty episode list into null: null means "whole title" to the worker. The normal
            // path finalizes an all-imported replacement; retaining the current scope is the safe fallback if
            // a caller asks to defer after everything already landed.
            if (remaining.Count > 0 && remaining.Count < requested.Count)
            {
                j.RequestedEpisodesCsv = string.Join(",", remaining
                    .OrderBy(x => x.Season).ThenBy(x => x.Episode)
                    .Select(x => $"S{x.Season}E{x.Episode}"));
                _logger.LogInformation(
                    "Replacement job {JobId}: {Imported} target(s) already imported; next search narrowed to {Remaining}",
                    j.Id, requested.Count - remaining.Count, j.RequestedEpisodesCsv);
            }
        }
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
        => await EnqueueFileReplacementAsync(request, target, replacePaths, episodes, mediaIssueId: null) is not null;

    public Task<int?> EnqueueReplacementAsync(MediaRequestDto request, Quality floor,
        IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes, int mediaIssueId) =>
        EnqueueFileReplacementAsync(request, floor, replacePaths, episodes, mediaIssueId);

    private async Task<int?> EnqueueFileReplacementAsync(MediaRequestDto request, Quality target,
        IReadOnlyList<string> replacePaths, IReadOnlyList<(int season, int episode)> episodes, int? mediaIssueId)
    {
        // The database also enforces this invariant. Checking here gives callers an idempotent result instead
        // of turning a concurrent/live job into an exception.
        var activeJob = await _db.FulfillmentJobs.AnyAsync(j =>
            j.MediaRequestId == request.Id &&
            (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
             j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred));
        if (activeJob) return null;

        // Copy identity fields (IMDb id / year / genres / anime flag) from the most recent real job so the
        // ranker can search without another metadata lookup; fall back to the request where needed.
        var origin = await _db.FulfillmentJobs
            .Where(j => j.MediaRequestId == request.Id && !j.IsUpgrade)
            .OrderByDescending(j => j.Id).FirstOrDefaultAsync();
        var requestEntity = await _db.MediaRequests.FirstOrDefaultAsync(r => r.Id == request.Id);

        // Old jobs predate anime classification, named destinations, quality profiles, and per-series
        // episode ordering. A replacement created from one of those rows must snapshot today's durable
        // request/config context at enqueue time; leaving the fields blank makes the worker guess at import
        // time and can route anime into ordinary TV or interpret an absolute-order pack as aired order.
        var externalId = !string.IsNullOrWhiteSpace(request.ExternalId)
            ? request.ExternalId
            : requestEntity?.ExternalId;
        var externalSource = !string.IsNullOrWhiteSpace(request.ExternalSource)
            ? request.ExternalSource
            : requestEntity?.ExternalSource;
        var mediaRef = request.MediaRef is { IsValid: true } suppliedRef
            ? suppliedRef
            : !string.IsNullOrWhiteSpace(externalId)
                ? MediaRef.FromExternal(externalSource ?? "external", externalId, request.MediaType,
                    request.RequestScopeKind.ToMediaKind(request.MediaType))
                : MediaRef.FromTmdb(request.MediaId, request.MediaType);
        var genres = string.IsNullOrWhiteSpace(origin?.GenresCsv)
            ? new List<string>()
            : origin.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        var durableAnime = request.IsAnime ?? requestEntity?.IsAnime;
        MediaDetailDto? refreshedDetail = null;
        if ((durableAnime is null || genres.Count == 0) && mediaRef.Provider == "tmdb")
        {
            try
            {
                refreshedDetail = await _metadata.GetDetailsAsync(mediaRef);
                if (refreshedDetail is not null)
                {
                    if (genres.Count == 0) genres = refreshedDetail.Genres.ToList();
                    durableAnime ??= refreshedDetail.IsAnime
                        ?? AnimeClassifier.IsAnime(refreshedDetail.Genres,
                            refreshedDetail.Languages, refreshedDetail.Countries);
                    if (requestEntity is { IsAnime: null }) requestEntity.IsAnime = durableAnime;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Could not refresh classification while creating replacement context for request {RequestId}",
                    request.Id);
            }
        }
        var isAnime = origin?.IsAnime == true || durableAnime == true || request.MediaType == MediaType.Anime;
        var mediaKind = origin?.MediaKind ?? request.RequestScopeKind.ToMediaKind(request.MediaType);
        if (request.MediaType is MediaType.TvShow or MediaType.Anime) mediaKind = MediaKind.Series;
        var requestScope = origin?.RequestScopeKind ?? requestEntity?.RequestScopeKind ?? request.RequestScopeKind;
        if (mediaKind == MediaKind.Series && episodes.Count > 0) requestScope = RequestScopeKind.Episodes;
        var tmdbId = origin?.TmdbId
            ?? (mediaRef.TryGetTmdbId(out var resolvedTmdbId) ? resolvedTmdbId : null);

        var profileId = request.QualityProfileId ?? requestEntity?.QualityProfileId ?? origin?.QualityProfileId;
        if (profileId is null)
        {
            profileId = await _profiles.ResolveProfileIdAsync(request.MediaType, request.MediaId, genres,
                requesterChoiceId: null, userId: requestEntity?.RequestedByUserId, isAnime: isAnime);
            if (requestEntity is not null) requestEntity.QualityProfileId = profileId;
        }

        var episodeOrderJson = origin?.EpisodeOrderProfileJson;
        var hasDestinationSnapshot = HasCompleteDestinationSnapshot(origin, request.MediaType);
        LibraryOrganizationPreferencesDto? currentSettings = null;
        if (string.IsNullOrWhiteSpace(episodeOrderJson) || !hasDestinationSnapshot)
            currentSettings = await _libraryPreferences.GetAsync();
        if (string.IsNullOrWhiteSpace(episodeOrderJson)
            && currentSettings is not null
            && EpisodeOrderMapping.Resolve(currentSettings.SeriesEpisodeOrderProfiles, tmdbId) is { } episodeProfile)
            episodeOrderJson = JsonSerializer.Serialize(episodeProfile);

        LibraryDestinationSnapshotDto? destination = null;
        if (!hasDestinationSnapshot)
        {
            destination = LibraryRouting.Resolve(currentSettings!, request.MediaType, target, genres, isAnime,
                isEpisode: mediaKind == MediaKind.Series,
                preferredDestinationId: origin?.LibraryDestinationId
                    ?? request.LibraryDestinationId ?? requestEntity?.LibraryDestinationId);
            if (requestEntity is not null) requestEntity.LibraryDestinationId = destination.Id;
        }

        var episodesCsv = episodes.Count > 0
            ? string.Join(",", episodes.Select(e => $"S{e.season}E{e.episode}"))
            : null;
        var upgradePolicyJson = origin?.MediaLanguagePolicyJson;
        if (string.IsNullOrWhiteSpace(upgradePolicyJson) && profileId is int upgradeProfileId)
        {
            var currentPolicy = MediaLanguagePolicy.FromProfile(await _profiles.GetProfileAsync(upgradeProfileId));
            if (MediaLanguagePolicy.IsActive(currentPolicy))
                upgradePolicyJson = JsonSerializer.Serialize(currentPolicy);
        }

        var job = new FulfillmentJobEntity
        {
            MediaRequestId = request.Id,
            MediaIdentityId = origin?.MediaIdentityId ?? requestEntity?.MediaIdentityId,
            MediaId = request.MediaId,
            MediaType = request.MediaType,
            MediaKind = mediaKind,
            RequestScopeKind = requestScope,
            AcquisitionContextJson = origin?.AcquisitionContextJson,
            Title = request.Title,
            Year = origin?.Year ?? refreshedDetail?.Year,
            TmdbId = tmdbId,
            ImdbId = origin?.ImdbId ?? refreshedDetail?.ImdbId,
            TvdbId = origin?.TvdbId ?? refreshedDetail?.TvdbId,
            ExternalId = externalId,
            ExternalSource = externalSource,
            RequestedEpisodesCsv = episodesCsv,
            CanonicalSeasonsJson = origin?.CanonicalSeasonsJson,
            Quality = target,
            QualityProfileId = profileId,
            MediaLanguagePolicyJson = upgradePolicyJson,
            EpisodeOrderProfileJson = episodeOrderJson,
            GenresCsv = genres.Count > 0 ? string.Join(",", genres) : null,
            IsAnime = isAnime,
            LibraryDestinationId = hasDestinationSnapshot ? origin!.LibraryDestinationId : destination!.Id,
            LibraryDestinationName = hasDestinationSnapshot ? origin!.LibraryDestinationName : destination!.Name,
            LibraryDestinationKind = hasDestinationSnapshot ? origin!.LibraryDestinationKind : destination!.ContentKind,
            LibraryDestinationRootPath = hasDestinationSnapshot ? origin!.LibraryDestinationRootPath : destination!.RootPath,
            LibraryPlexSectionId = hasDestinationSnapshot ? origin!.LibraryPlexSectionId : destination!.PlexSectionId,
            LibraryDestinationTemplate = hasDestinationSnapshot ? origin!.LibraryDestinationTemplate : destination!.Template,
            LibrarySeasonPackFolderTemplate = hasDestinationSnapshot
                ? origin!.LibrarySeasonPackFolderTemplate : destination!.SeasonPackFolderTemplate,
            IsUpgrade = true,
            IsReplacement = mediaIssueId.HasValue,
            MediaIssueId = mediaIssueId,
            ReplacePathsJson = replacePaths.Count > 0 ? JsonSerializer.Serialize(replacePaths) : null,
            Status = FulfillmentStatus.Queued,
            CreatedAt = DateTime.UtcNow
        };
        _db.FulfillmentJobs.Add(job);
        try
        {
            await _db.SaveChangesAsync();
            return job.Id;
        }
        catch (DbUpdateException)
        {
            _db.Entry(job).State = EntityState.Detached;
            if (await _db.FulfillmentJobs.AnyAsync(j => j.MediaRequestId == request.Id &&
                    (j.Status == FulfillmentStatus.Queued || j.Status == FulfillmentStatus.Claimed ||
                     j.Status == FulfillmentStatus.Downloading || j.Status == FulfillmentStatus.Deferred)))
                return null;
            throw;
        }
    }

    private static bool HasCompleteDestinationSnapshot(FulfillmentJobEntity? job, MediaType mediaType) =>
        job is not null
        && !string.IsNullOrWhiteSpace(job.LibraryDestinationId)
        && job.LibraryDestinationKind == LibraryRouting.ContentKind(mediaType)
        && !string.IsNullOrWhiteSpace(job.LibraryDestinationRootPath)
        && !string.IsNullOrWhiteSpace(job.LibraryDestinationTemplate);

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

    private static IEnumerable<(int Season, int Episode)> AuditCoverage(ImportedFileEntity file) =>
        file.EpisodeCoverage.Count > 0
            ? file.EpisodeCoverage.Select(x => (x.SeasonNumber, x.EpisodeNumber))
            : file.SeasonNumber is int season && file.EpisodeNumber is int episode
                ? [(season, episode)]
                : [];

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
        Media = !string.IsNullOrWhiteSpace(j.ExternalId)
            ? MediaRef.FromExternal(j.ExternalSource ?? "external", j.ExternalId, j.MediaType,
                j.MediaKind.BelongsTo(j.MediaType) ? j.MediaKind : j.RequestScopeKind.ToMediaKind(j.MediaType))
            : MediaRef.FromTmdb(j.MediaId, j.MediaType),
        RequestScope = Enum.IsDefined(j.RequestScopeKind) &&
                       (j.RequestScopeKind != RequestScopeKind.Title || j.MediaType == MediaType.Movie)
            ? j.RequestScopeKind
            : j.MediaType is MediaType.TvShow or MediaType.Anime
            ? !string.IsNullOrWhiteSpace(j.RequestedEpisodesCsv) ? RequestScopeKind.Episodes
            : !string.IsNullOrWhiteSpace(j.RequestedSeasonsCsv) ? RequestScopeKind.Seasons
            : RequestScopeKind.Series
            : j.MediaType == MediaType.Music ? RequestScopeKind.Album : RequestScopeKind.Title,
        Music = string.IsNullOrWhiteSpace(j.AcquisitionContextJson)
            ? null
            : JsonSerializer.Deserialize<MusicAcquisitionContextDto>(j.AcquisitionContextJson),
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
        CanonicalSeasons = string.IsNullOrWhiteSpace(j.CanonicalSeasonsJson)
            ? new List<CanonicalSeasonIdentityDto>()
            : (JsonSerializer.Deserialize<List<CanonicalSeasonIdentityDto>>(j.CanonicalSeasonsJson)
               ?? new List<CanonicalSeasonIdentityDto>()),
        Quality = j.Quality,
        MediaLanguagePolicy = string.IsNullOrWhiteSpace(j.MediaLanguagePolicyJson)
            ? null
            : JsonSerializer.Deserialize<MediaLanguagePolicyDto>(j.MediaLanguagePolicyJson),
        EpisodeOrderProfile = string.IsNullOrWhiteSpace(j.EpisodeOrderProfileJson)
            ? null
            : JsonSerializer.Deserialize<SeriesEpisodeOrderProfileDto>(j.EpisodeOrderProfileJson),
        EmptySearchCount = j.EmptySearchCount,
        IsManualGrab = j.IsManualGrab,
        ForcedMagnet = j.ForcedMagnet,
        ForcedReleaseName = j.ForcedReleaseName,
        ForcedIndexerId = j.ForcedIndexerId,
        Genres = string.IsNullOrWhiteSpace(j.GenresCsv)
            ? new List<string>()
            : j.GenresCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList(),
        IsAnime = j.IsAnime,
        LibraryDestination = !string.IsNullOrWhiteSpace(j.LibraryDestinationId)
            && !string.IsNullOrWhiteSpace(j.LibraryDestinationRootPath)
            && j.LibraryDestinationKind.HasValue
            ? new LibraryDestinationSnapshotDto
            {
                Id = j.LibraryDestinationId,
                Name = j.LibraryDestinationName ?? j.LibraryDestinationId,
                ContentKind = j.LibraryDestinationKind.Value,
                RootPath = j.LibraryDestinationRootPath,
                PlexSectionId = j.LibraryPlexSectionId,
                Template = j.LibraryDestinationTemplate ?? string.Empty,
                SeasonPackFolderTemplate = j.LibrarySeasonPackFolderTemplate
            }
            : null,
        Status = j.Status,
        Attempts = j.Attempts,
        Progress = j.Progress,
        IsUpgrade = j.IsUpgrade,
        IsReplacement = j.IsReplacement,
        MediaIssueId = j.MediaIssueId,
        ReplacePaths = string.IsNullOrWhiteSpace(j.ReplacePathsJson)
            ? new List<string>()
            : (JsonSerializer.Deserialize<List<string>>(j.ReplacePathsJson) ?? new List<string>())
    };

    private static MusicAcquisitionContextDto BuildMusicContext(MediaKind kind, string title, MediaDetailDto? detail)
    {
        var artist = kind == MediaKind.Artist ? detail?.Title ?? title : detail?.Music?.ArtistCredit;
        return new MusicAcquisitionContextDto
        {
            SchemaVersion = 2,
            Kind = kind,
            Artist = artist,
            Album = kind == MediaKind.Album ? detail?.Title ?? title
                : kind == MediaKind.Track ? detail?.Music?.AlbumTitle : null,
            Track = kind == MediaKind.Track ? detail?.Title ?? title : null,
            ArtworkUrl = detail?.PosterUrl,
            TrackCount = detail?.Music?.TrackCount ?? 0,
            Tracks = detail?.Music?.Tracks.Select(t => new MusicTrackMetadataDto
            {
                RecordingId = t.RecordingId,
                Title = t.Title,
                ArtistCredit = t.ArtistCredit,
                DiscNumber = t.DiscNumber,
                TrackNumber = t.TrackNumber,
                DurationMs = t.DurationMs
            }).ToList() ?? new(),
            ExpectedAlbums = detail?.Music?.AlbumTitles.ToList() ?? new()
        };
    }
}
