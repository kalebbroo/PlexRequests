using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Populates the quality-tier catalog and converts the old <see cref="QualityRuleEntity"/> rows into
/// profiles + assignment rules, then backfills every row that needs a profile.
///
/// This lives in application code rather than SQL inside the migration because it needs LINQ, the enums and
/// the rule-matching semantics — expressing "evaluate the ported assignment rules against each request's
/// genres" in raw SQLite would be both unreadable and untestable. It follows the same
/// run-once-on-startup-and-be-idempotent shape as <c>IndexerAdminService.SeedAsync</c> and
/// <c>JobSchedulerService.SeedScheduleAsync</c>, so a restart mid-way is safe and re-running is a no-op.
///
/// The old <see cref="QualityRuleEntity"/> table is deliberately left intact: nothing reads it after this,
/// but keeping it for a release means a rollback doesn't need a database restore.
/// </summary>
public class QualityProfileSeeder(IDbContextFactory<AppDbContext> dbFactory, ILogger<QualityProfileSeeder> logger)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // The resolutions that get tiers. Quality.Any is not a tier — it's the absence of a target.
    private static readonly Quality[] Resolutions =
        { Quality.SD, Quality.HD, Quality.FullHD, Quality.UHD4K, Quality.UHD8K };

    // Every source, INCLUDING Unknown. That row is not optional: ReleaseParser returns Unknown for a large
    // share of real release names, and without a tier to land on those releases would be unrankable.
    private static readonly ReleaseSource[] Sources =
        { ReleaseSource.Unknown, ReleaseSource.Cam, ReleaseSource.Hdtv, ReleaseSource.WebRip,
          ReleaseSource.WebDl, ReleaseSource.BluRay, ReleaseSource.Remux };

    public async Task SeedAsync(CancellationToken ct = default)
    {
        await using var db = await dbFactory.CreateDbContextAsync(ct);

        var definitions = await SeedDefinitionsAsync(db, ct);
        await SeedProfilesAsync(db, definitions, ct);
        await SimplifyStockProfilesAsync(db, definitions, ct);
        await PortQualityRulesAsync(db, definitions, ct);
        await BackfillAsync(db, ct);
    }

    // ---- Tier catalog -------------------------------------------------------------------------------

    private async Task<List<QualityDefinitionEntity>> SeedDefinitionsAsync(AppDbContext db, CancellationToken ct)
    {
        var existing = await db.QualityDefinitions.ToListAsync(ct);
        var byKey = existing.ToDictionary(d => ((int)d.Resolution, d.Source));
        int added = 0;

        foreach (var res in Resolutions)
        {
            foreach (var src in Sources)
            {
                if (byKey.ContainsKey(((int)res, src))) continue;
                var def = new QualityDefinitionEntity
                {
                    Name = TierName(res, src),
                    Resolution = (int)res,
                    Source = src,
                    SortWeight = (int)res * 10 + (int)src,
                    IsSystem = true
                };
                db.QualityDefinitions.Add(def);
                byKey[((int)res, src)] = def;
                added++;
            }
        }

        if (added > 0)
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Seeded {Count} quality tier definition(s)", added);
        }
        return byKey.Values.OrderBy(d => d.SortWeight).ToList();
    }

    /// <summary>"WEBDL-1080p", "Bluray-720p", "Unknown-1080p".</summary>
    private static string TierName(Quality res, ReleaseSource src)
    {
        var label = src switch
        {
            ReleaseSource.Unknown => "Unknown",
            ReleaseSource.Cam => "CAM",
            ReleaseSource.Hdtv => "HDTV",
            ReleaseSource.WebRip => "WEBRip",
            ReleaseSource.WebDl => "WEBDL",
            ReleaseSource.BluRay => "Bluray",
            ReleaseSource.Remux => "Remux",
            _ => src.ToString()
        };
        return $"{label}-{res.Label()}";
    }

    // ---- Profiles -----------------------------------------------------------------------------------

    /// <summary>Seeds one useful default. Extra profiles are for genuine exceptions, not basic setup.</summary>
    private async Task SeedProfilesAsync(AppDbContext db, List<QualityDefinitionEntity> defs, CancellationToken ct)
    {
        if (await db.QualityProfiles.AnyAsync(ct)) return;

        db.QualityProfiles.Add(BuildProfile("Everyday", defs, floor: Quality.HD,
            cutoff: Quality.FullHD, isDefault: true, sortOrder: 0));
        await db.SaveChangesAsync(ct);
        logger.LogInformation("Seeded the Everyday release profile");
    }

    /// <summary>
    /// Earlier versions exposed three overlapping stock profiles even though almost every request used one.
    /// Collapse that choice for new requests without rewriting historical request assignments: the catch-all
    /// becomes Everyday and the two legacy alternatives remain valid but stop appearing in the requester UI.
    /// Name checks make this a one-time, conservative migration that never touches administrator-created rows.
    /// </summary>
    private async Task SimplifyStockProfilesAsync(AppDbContext db,
        List<QualityDefinitionEntity> definitions, CancellationToken ct)
    {
        var profiles = await db.QualityProfiles.ToListAsync(ct);
        var catchAll = profiles.FirstOrDefault(p => p.Name == "Any");
        if (catchAll is not null)
        {
            var everyday = BuildProfile("Everyday", definitions, floor: Quality.HD,
                cutoff: Quality.FullHD, isDefault: true, sortOrder: catchAll.SortOrder);
            catchAll.Name = "Everyday";
            catchAll.IsDefault = true;
            catchAll.IsUserSelectable = true;
            catchAll.ItemsJson = everyday.ItemsJson;
            catchAll.CutoffQualityDefinitionId = everyday.CutoffQualityDefinitionId;
            catchAll.UpgradeAllowed = true;
            catchAll.LanguagePreference = ReleaseLanguagePreference.Smart;
            catchAll.PreferredAudioLanguage = "en";
            catchAll.PreferredSubtitleLanguage = "en";
            catchAll.PreferForcedSubtitles = true;
            catchAll.SetPreferredTracksAsDefault = true;
            // The previous English preset was a hard requirement. Smart keeps English first without
            // stranding foreign films or Japanese-only anime.
            catchAll.RequiredAudioLanguagesCsv = null;
            catchAll.AllowedLanguagesCsv = null;
            catchAll.RequiredSubtitleLanguagesCsv = null;
            catchAll.RequireForcedSubtitle = false;
            catchAll.AllowUnknownTrackLanguage = true;
            catchAll.UpdatedAt = DateTime.UtcNow;

            foreach (var profile in profiles.Where(p => p.Id != catchAll.Id))
                profile.IsDefault = false;
        }

        foreach (var legacy in profiles.Where(p => p.Name is "HD-1080p" or "Ultra-HD"))
        {
            var needsUpdate = legacy.IsUserSelectable
                              || legacy.LanguagePreference != ReleaseLanguagePreference.Smart
                              || legacy.PreferredAudioLanguage is null
                              || legacy.PreferredSubtitleLanguage is null
                              || !legacy.PreferForcedSubtitles;
            if (!needsUpdate) continue;
            legacy.IsUserSelectable = false;
            legacy.LanguagePreference = ReleaseLanguagePreference.Smart;
            legacy.PreferredAudioLanguage ??= "en";
            legacy.PreferredSubtitleLanguage ??= "en";
            legacy.PreferForcedSubtitles = true;
            legacy.UpdatedAt = DateTime.UtcNow;
        }

        // PR #127 exposed a legacy-install edge case: a default Quality.Any rule could create this derived
        // row after Any had already been renamed, then make it default. It never represented an intentional
        // custom profile. Retire that orphan and restore Everyday once; referenced imported profiles are
        // left alone because they may represent a real historical override.
        var everydayProfile = catchAll ?? profiles.FirstOrDefault(p => p.Name == "Everyday");
        var importedAny = profiles.FirstOrDefault(p => p.Name == "Unknown (imported)" && p.IsDefault);
        if (everydayProfile is not null && importedAny is not null)
        {
            var referenced = await db.MediaRequests.AnyAsync(r => r.QualityProfileId == importedAny.Id, ct)
                             || await db.ProfileAssignmentRules.AnyAsync(r => r.QualityProfileId == importedAny.Id, ct);
            if (!referenced)
            {
                foreach (var profile in profiles) profile.IsDefault = profile.Id == everydayProfile.Id;
                importedAny.IsUserSelectable = false;
                importedAny.UpdatedAt = DateTime.UtcNow;
            }
        }

        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(ct);
            logger.LogInformation("Simplified legacy stock profiles into the Everyday default");
        }
    }

    /// <summary>
    /// A profile allowing every tier from <paramref name="floor"/> upward, with the cutoff set at the WEBDL
    /// tier of <paramref name="cutoff"/> grouped with its Bluray sibling (the two are interchangeable in
    /// practice, and treating them as one stops a pointless Bluray-for-WEBDL "upgrade").
    /// CAM tiers are never allowed regardless of floor.
    /// </summary>
    private static QualityProfileEntity BuildProfile(string name, List<QualityDefinitionEntity> defs,
        Quality floor, Quality cutoff, bool isDefault, int sortOrder)
    {
        var items = new List<ProfileItem>();
        var cutoffKey = string.Empty;

        foreach (var res in Resolutions)
        {
            var allowed = (int)res >= (int)floor;
            foreach (var src in Sources)
            {
                if (src == ReleaseSource.Cam) continue; // never wanted; omitted rather than listed-and-disallowed
                var def = defs.First(d => d.Resolution == (int)res && d.Source == src);

                // Group WEBDL + Bluray at each resolution — equivalent quality from the viewer's side.
                if (src == ReleaseSource.WebDl)
                {
                    var bluray = defs.First(d => d.Resolution == (int)res && d.Source == ReleaseSource.BluRay);
                    var key = $"g:hq{(int)res}";
                    items.Add(new ProfileItem(key, $"WEBDL/Bluray-{res.Label()}", allowed, new[] { def.Id, bluray.Id }));
                    if (res == cutoff) cutoffKey = key;
                    continue;
                }
                if (src == ReleaseSource.BluRay) continue; // folded into the group above

                items.Add(new ProfileItem($"q:{def.Id}", def.Name, allowed, null));
            }
        }

        // Rank ascending from worst, so index 0 is the least desirable tier.
        items = items.OrderBy(i => TierWeight(i, defs)).ToList();

        var cutoffGroup = items.FirstOrDefault(i => i.K == cutoffKey);
        var cutoffDefId = cutoffGroup?.Members?.FirstOrDefault()
                          ?? defs.First(d => d.Resolution == (int)cutoff && d.Source == ReleaseSource.WebDl).Id;

        return new QualityProfileEntity
        {
            Name = name,
            IsDefault = isDefault,
            IsUserSelectable = true,
            AppliesToMediaTypes = (int)MediaTypeFlags.All,
            SortOrder = sortOrder,
            ItemsJson = JsonSerializer.Serialize(items, Json),
            CutoffQualityDefinitionId = cutoffDefId,
            UpgradeAllowed = true,
            LanguagePreference = ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = "en",
            PreferredSubtitleLanguage = "en",
            PreferForcedSubtitles = true,
            SetPreferredTracksAsDefault = true
        };
    }

    private static int TierWeight(ProfileItem item, List<QualityDefinitionEntity> defs)
    {
        var id = item.Members?.FirstOrDefault()
                 ?? (int.TryParse(item.K.AsSpan(2), out var parsed) ? parsed : 0);
        return defs.FirstOrDefault(d => d.Id == id)?.SortWeight ?? 0;
    }

    // ---- Porting the legacy rules -------------------------------------------------------------------

    /// <summary>
    /// Turn each legacy <see cref="QualityRuleEntity"/> into (a profile for its target quality, reusing one
    /// when several rules share a target) plus an assignment rule carrying its conditions verbatim. Rules
    /// collapsing onto a shared profile is intended — what differed between them was the conditions, and
    /// those are preserved one-for-one.
    /// </summary>
    private async Task PortQualityRulesAsync(AppDbContext db, List<QualityDefinitionEntity> defs, CancellationToken ct)
    {
        // Idempotent on its own rather than piggybacking on "did we just create the profiles?" — otherwise a
        // deployment that ends up with profiles before this runs would silently never import its rules.
        // Caveat: deleting every assignment rule by hand and restarting re-imports them (unconditional ones
        // still arrive disabled). That's a rare, benign and visible outcome, so it doesn't warrant a marker table.
        if (await db.ProfileAssignmentRules.AnyAsync(ct)
            || await db.QualityProfiles.AnyAsync(p => p.Name.EndsWith(" (imported)"), ct)) return;

        var legacy = await db.QualityRules.OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(ct);
        if (legacy.Count == 0) return;

        var profiles = await db.QualityProfiles.ToListAsync(ct);
        var byTarget = new Dictionary<Quality, QualityProfileEntity>();

        // The legacy default rule's target decides which profile is the system default.
        var legacyDefault = legacy.FirstOrDefault(r => r.IsDefault);

        foreach (var target in legacy.Select(r => r.TargetQuality).Distinct())
        {
            var stock = target switch
            {
                Quality.FullHD => profiles.FirstOrDefault(p => p.Name is "Everyday" or "HD-1080p"),
                Quality.UHD4K => profiles.FirstOrDefault(p => p.Name == "Ultra-HD"),
                Quality.Any => profiles.FirstOrDefault(p => p.Name is "Everyday" or "Any"),
                _ => null
            };
            if (stock is not null) { byTarget[target] = stock; continue; }

            var created = BuildProfile($"{target.Label()} (imported)", defs,
                floor: target == Quality.Any ? Quality.SD : target,
                cutoff: target == Quality.Any ? Quality.FullHD : target,
                isDefault: false,
                sortOrder: 10 + (int)target);
            db.QualityProfiles.Add(created);
            byTarget[target] = created;
        }
        await db.SaveChangesAsync(ct);

        if (legacyDefault is not null && byTarget.TryGetValue(legacyDefault.TargetQuality, out var newDefault))
        {
            foreach (var p in await db.QualityProfiles.ToListAsync(ct)) p.IsDefault = p.Id == newDefault.Id;
        }

        int ported = 0;
        foreach (var rule in legacy.Where(r => !r.IsDefault))
        {
            var profile = byTarget[rule.TargetQuality];
            db.ProfileAssignmentRules.Add(new ProfileAssignmentRuleEntity
            {
                Name = rule.Name,
                Order = rule.Order,
                // An imported rule with no conditions matched every title — which is exactly the footgun
                // that pinned this deployment's downloads to one quality. Carry it over disabled so the
                // behaviour is visible and opt-in rather than silently preserved.
                Enabled = rule.Enabled && !IsUnconditional(rule),
                MatchMediaType = rule.MatchMediaType,
                MatchGenre = rule.MatchGenre,
                MatchTmdbId = rule.MatchTmdbId,
                MatchLibrary = rule.MatchLibrary,
                QualityProfileId = profile.Id
            });
            if (rule.Enabled && IsUnconditional(rule))
                logger.LogWarning("Imported quality rule \"{Name}\" had no match conditions, so it applied to every title (target {Target}). It has been carried over DISABLED — re-enable it in Admin > Downloads > Profile assignment if that was intended.",
                    rule.Name, rule.TargetQuality);
            ported++;
        }

        await db.SaveChangesAsync(ct);
        logger.LogInformation("Ported {Count} legacy quality rule(s) into {Profiles} profile(s)", ported, byTarget.Count);
    }

    private static bool IsUnconditional(QualityRuleEntity r) =>
        r.MatchMediaType is null && r.MatchTmdbId is null
        && string.IsNullOrWhiteSpace(r.MatchGenre) && string.IsNullOrWhiteSpace(r.MatchLibrary);

    // ---- Backfill -----------------------------------------------------------------------------------

    private async Task BackfillAsync(AppDbContext db, CancellationToken ct)
    {
        var defaultProfile = await db.QualityProfiles.FirstOrDefaultAsync(p => p.IsDefault, ct)
                             ?? await db.QualityProfiles.OrderBy(p => p.SortOrder).FirstOrDefaultAsync(ct);
        if (defaultProfile is null) return;

        var rules = await db.ProfileAssignmentRules.Where(r => r.Enabled)
            .OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync(ct);

        // Requests: evaluate the ported rules against each request's genre snapshot, else the default.
        var requests = await db.MediaRequests.Where(r => r.QualityProfileId == null).ToListAsync(ct);
        if (requests.Count > 0)
        {
            // Genres come from the newest fulfillment job per request — the same snapshot the upgrade scan
            // already uses, so rule evaluation here agrees with rule evaluation there.
            var genreByRequest = await db.FulfillmentJobs
                .GroupBy(j => j.MediaRequestId)
                .Select(g => new { RequestId = g.Key, GenresCsv = g.OrderByDescending(j => j.Id).First().GenresCsv })
                .ToDictionaryAsync(x => x.RequestId, x => x.GenresCsv, ct);

            foreach (var req in requests)
            {
                genreByRequest.TryGetValue(req.Id, out var csv);
                var genres = string.IsNullOrWhiteSpace(csv)
                    ? Array.Empty<string>()
                    : csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var match = rules.FirstOrDefault(r => Matches(r, req.MediaType, req.MediaId, genres));
                req.QualityProfileId = match?.QualityProfileId ?? defaultProfile.Id;
            }
            logger.LogInformation("Assigned a quality profile to {Count} existing request(s)", requests.Count);
        }

        // Persist the request assignments before anything reads them back. Without this the job lookup
        // below issues a fresh query that can't see the still-tracked changes, finds nothing, and quietly
        // gives every job the default profile instead of its request's — so a title with a genre-specific
        // profile would have been searched at the wrong quality.
        if (requests.Count > 0) await db.SaveChangesAsync(ct);

        // In-flight jobs inherit their request's profile.
        var jobs = await db.FulfillmentJobs.Where(j => j.QualityProfileId == null).ToListAsync(ct);
        if (jobs.Count > 0)
        {
            var profileByRequest = await db.MediaRequests
                .Where(r => r.QualityProfileId != null)
                .ToDictionaryAsync(r => r.Id, r => r.QualityProfileId!.Value, ct);
            foreach (var job in jobs)
                job.QualityProfileId = profileByRequest.TryGetValue(job.MediaRequestId, out var pid) ? pid : defaultProfile.Id;
            logger.LogInformation("Assigned a quality profile to {Count} existing fulfillment job(s)", jobs.Count);
        }

        // Imported files get the tier matching their recorded resolution. Source is genuinely unknown for
        // historical rows (the release name wasn't stored), so they land on the Unknown-source tier rather
        // than being guessed into a source they may not have had.
        var files = await db.ImportedFiles
            .Where(f => f.QualityDefinitionId == null && f.ResolutionHeight > 0)
            .ToListAsync(ct);
        if (files.Count > 0)
        {
            var defByResolution = await db.QualityDefinitions
                .Where(d => d.Source == ReleaseSource.Unknown)
                .ToDictionaryAsync(d => d.Resolution, d => d.Id, ct);
            int matched = 0;
            foreach (var f in files)
            {
                var tier = (int)QualityHelper.FromHeight(f.ResolutionHeight);
                if (defByResolution.TryGetValue(tier, out var defId)) { f.QualityDefinitionId = defId; matched++; }
            }
            logger.LogInformation("Tagged {Count} imported file(s) with a quality tier", matched);
        }

        if (db.ChangeTracker.HasChanges()) await db.SaveChangesAsync(ct);

        // The invariant this whole seeder exists to establish: every request is bound to a profile. It isn't
        // enforced by the schema — making the column non-nullable needs a SQLite table rebuild plus a
        // restricting FK, and on a deployment where the profiles migration and that constraint land in the
        // same startup pass, QualityProfiles is still empty when the constraint applies and the app fails to
        // start. So it's asserted here instead, where a violation is a loud log line rather than an outage.
        var unbound = await db.MediaRequests.CountAsync(r => r.QualityProfileId == null, ct);
        if (unbound > 0)
            logger.LogError("{Count} request(s) still have no quality profile after seeding — they will fall " +
                            "back to the default at enqueue time instead of using their assigned profile", unbound);
    }

    /// <summary>Rule matching — every non-null condition must match. Mirrors the legacy semantics exactly.</summary>
    internal static bool Matches(ProfileAssignmentRuleEntity r, MediaType mediaType, int tmdbId,
        IReadOnlyCollection<string> genres, string? library = null, bool? isAnime = null)
    {
        if (r.MatchMediaType == MediaType.Anime)
        {
            // Rolling compatibility for old Anime-as-a-media-type rules: keep them series-only and make
            // their classification requirement explicit when matching truthful TV-shaped anime requests.
            if (mediaType is not (MediaType.TvShow or MediaType.Anime) || isAnime != true) return false;
        }
        else if (r.MatchMediaType.HasValue && r.MatchMediaType.Value != mediaType) return false;
        if (r.MatchAnime.HasValue && (!isAnime.HasValue || r.MatchAnime.Value != isAnime.Value)) return false;
        if (r.MatchTmdbId.HasValue && r.MatchTmdbId.Value != tmdbId) return false;
        if (!string.IsNullOrWhiteSpace(r.MatchGenre)
            && !genres.Contains(r.MatchGenre, StringComparer.OrdinalIgnoreCase)) return false;
        if (!string.IsNullOrWhiteSpace(r.MatchLibrary)
            && !string.Equals(r.MatchLibrary, library, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    /// <summary>One entry in <see cref="QualityProfileEntity.ItemsJson"/>.</summary>
    internal sealed record ProfileItem(string K, string N, bool Allowed, int[]? Members);
}
