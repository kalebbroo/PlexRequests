using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IQualityProfileService
{
    Task<List<QualityDefinitionDto>> GetDefinitionsAsync();
    Task<List<QualityProfileDto>> GetProfilesAsync();
    Task<List<QualityProfileSummaryDto>> GetSelectableProfilesAsync(int? userId, MediaType mediaType);
    Task<QualityProfileDto?> GetProfileAsync(int id);
    Task<QualityProfileDto> CreateProfileAsync(string name);
    Task<bool> UpdateProfileAsync(QualityProfileDto profile);
    Task<(bool ok, string? error)> DeleteProfileAsync(int id);
    Task<bool> SetDefaultProfileAsync(int id);

    Task<List<ProfileAssignmentRuleDto>> GetAssignmentRulesAsync();
    Task<ProfileAssignmentRuleDto> CreateAssignmentRuleAsync();
    Task<(bool ok, string? error)> UpdateAssignmentRuleAsync(ProfileAssignmentRuleDto rule);
    Task<bool> DeleteAssignmentRuleAsync(int id);
    Task<bool> MoveAssignmentRuleAsync(int id, bool up);

    /// <summary>
    /// The profile a new request should use. Precedence: the requester's explicit pick (only when they're
    /// actually allowed it) -> the first matching assignment rule -> the default profile.
    /// </summary>
    Task<int> ResolveProfileIdAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres,
        int? requesterChoiceId, int? userId, string? library = null, bool? isAnime = null);

    /// <summary>The cutoff tier's resolution, used as the downloader's quality floor.</summary>
    Task<Quality> GetCutoffQualityAsync(int profileId);
}

/// <summary>
/// CRUD and resolution over quality profiles. Replaces <c>QualityRuleService</c>, whose model could only
/// express a single target quality per title. Ranking still consumes the cutoff resolution via
/// <see cref="GetCutoffQualityAsync"/>; the ordered tier list becomes load-bearing once the ranker moves
/// into the shared library.
/// </summary>
public class QualityProfileService(AppDbContext db, ILogger<QualityProfileService> logger) : IQualityProfileService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    // ---- Definitions --------------------------------------------------------------------------------

    public async Task<List<QualityDefinitionDto>> GetDefinitionsAsync() =>
        await db.QualityDefinitions.AsNoTracking().OrderBy(d => d.SortWeight)
            .Select(d => new QualityDefinitionDto
            {
                Id = d.Id, Name = d.Name, Resolution = d.Resolution, Source = d.Source, SortWeight = d.SortWeight
            }).ToListAsync();

    // ---- Profiles -----------------------------------------------------------------------------------

    public async Task<List<QualityProfileDto>> GetProfilesAsync()
    {
        var rows = await db.QualityProfiles.AsNoTracking().OrderBy(p => p.SortOrder).ThenBy(p => p.Id).ToListAsync();
        var inUse = await db.MediaRequests.Where(r => r.QualityProfileId != null)
            .Select(r => r.QualityProfileId!.Value).Distinct().ToListAsync();
        var ruleUse = await db.ProfileAssignmentRules.Select(r => r.QualityProfileId).Distinct().ToListAsync();
        return rows.Select(p => ToDto(p, inUse.Contains(p.Id) || ruleUse.Contains(p.Id))).ToList();
    }

    public async Task<QualityProfileDto?> GetProfileAsync(int id)
    {
        var p = await db.QualityProfiles.AsNoTracking().FirstOrDefaultAsync(x => x.Id == id);
        return p is null ? null : ToDto(p, false);
    }

    public async Task<List<QualityProfileSummaryDto>> GetSelectableProfilesAsync(int? userId, MediaType mediaType)
    {
        var flag = ToFlag(mediaType);
        var profiles = await db.QualityProfiles.AsNoTracking()
            .Where(p => p.IsUserSelectable && (p.AppliesToMediaTypes & (int)flag) != 0)
            .OrderBy(p => p.SortOrder).ThenBy(p => p.Id)
            .Select(p => new QualityProfileSummaryDto { Id = p.Id, Name = p.Name, IsDefault = p.IsDefault })
            .ToListAsync();

        // A per-user allow-list narrows this further, so a member can be capped below what admins may pick.
        var allowedCsv = userId is int uid
            ? await db.UserProfiles.Where(u => u.UserId == uid)
                .Select(u => u.UserGroupId != null
                    ? u.UserGroup!.AllowedQualityProfileIdsCsv
                    : u.AllowedQualityProfileIdsCsv)
                .FirstOrDefaultAsync()
            : null;
        if (string.IsNullOrWhiteSpace(allowedCsv)) return profiles;

        var allowed = ParseIds(allowedCsv);
        return allowed.Count == 0 ? profiles : profiles.Where(p => allowed.Contains(p.Id)).ToList();
    }

    public async Task<QualityProfileDto> CreateProfileAsync(string name)
    {
        var maxOrder = await db.QualityProfiles.MaxAsync(p => (int?)p.SortOrder) ?? 0;
        // Start from the default profile's tier list rather than an empty one — a profile with nothing
        // allowed can never match a release, and that's a confusing thing to hand someone as a starting point.
        var template = await db.QualityProfiles.AsNoTracking().FirstOrDefaultAsync(p => p.IsDefault);
        var entity = new QualityProfileEntity
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"New profile {maxOrder + 1}" : name.Trim(),
            SortOrder = maxOrder + 1,
            IsDefault = false,
            IsUserSelectable = true,
            LanguagePreference = template?.LanguagePreference ?? ReleaseLanguagePreference.Smart,
            PreferredAudioLanguage = template?.PreferredAudioLanguage ?? "en",
            PreferredSubtitleLanguage = template?.PreferredSubtitleLanguage ?? "en",
            PreferForcedSubtitles = template?.PreferForcedSubtitles ?? true,
            SetPreferredTracksAsDefault = template?.SetPreferredTracksAsDefault ?? true,
            AppliesToMediaTypes = (int)MediaTypeFlags.All,
            ItemsJson = template?.ItemsJson ?? "[]",
            CutoffQualityDefinitionId = template?.CutoffQualityDefinitionId
                ?? await db.QualityDefinitions.OrderBy(d => d.SortWeight).Select(d => d.Id).FirstOrDefaultAsync()
        };
        db.QualityProfiles.Add(entity);
        await db.SaveChangesAsync();
        return ToDto(entity, false);
    }

    public async Task<bool> UpdateProfileAsync(QualityProfileDto dto)
    {
        var p = await db.QualityProfiles.FirstOrDefaultAsync(x => x.Id == dto.Id);
        if (p is null) return false;

        p.Name = string.IsNullOrWhiteSpace(dto.Name) ? p.Name : dto.Name.Trim();
        p.IsUserSelectable = dto.IsUserSelectable;
        p.AppliesToMediaTypes = dto.AppliesToMediaTypes;
        p.UpgradeAllowed = dto.UpgradeAllowed;
        p.MinSizeGb = dto.MinSizeGb;
        p.MaxSizeGb = dto.MaxSizeGb;
        p.MaxSeasonPackSizeGb = dto.MaxSeasonPackSizeGb;
        p.MinSeeders = dto.MinSeeders;
        p.AllowedLanguagesCsv = string.IsNullOrWhiteSpace(dto.AllowedLanguagesCsv) ? null : dto.AllowedLanguagesCsv.Trim();
        p.RequiredAudioLanguagesCsv = string.IsNullOrWhiteSpace(dto.RequiredAudioLanguagesCsv)
            ? null : dto.RequiredAudioLanguagesCsv.Trim();
        p.RequiredSubtitleLanguagesCsv = string.IsNullOrWhiteSpace(dto.RequiredSubtitleLanguagesCsv)
            ? null : dto.RequiredSubtitleLanguagesCsv.Trim();
        p.RequireForcedSubtitle = dto.RequireForcedSubtitle;
        p.AllowUnknownTrackLanguage = dto.AllowUnknownTrackLanguage;
        p.LanguagePreference = dto.LanguagePreference;
        p.PreferredAudioLanguage = MediaLanguagePolicy.Normalize(dto.PreferredAudioLanguage);
        p.PreferredSubtitleLanguage = MediaLanguagePolicy.Normalize(dto.PreferredSubtitleLanguage);
        p.PreferForcedSubtitles = dto.PreferForcedSubtitles;
        p.SetPreferredTracksAsDefault = dto.SetPreferredTracksAsDefault;
        p.MinCustomFormatScore = dto.MinCustomFormatScore;
        p.CutoffFormatScore = dto.CutoffFormatScore;
        if (dto.Items is { Count: > 0 }) p.ItemsJson = JsonSerializer.Serialize(dto.Items, Json);

        // The cutoff must be a tier the profile actually allows, or the request can never reach it and the
        // upgrade scan hunts forever.
        if (dto.CutoffQualityDefinitionId > 0 && IsAllowedTier(p.ItemsJson, dto.CutoffQualityDefinitionId))
            p.CutoffQualityDefinitionId = dto.CutoffQualityDefinitionId;

        p.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<(bool ok, string? error)> DeleteProfileAsync(int id)
    {
        var p = await db.QualityProfiles.FirstOrDefaultAsync(x => x.Id == id);
        if (p is null) return (false, "Profile not found.");
        if (p.IsDefault) return (false, "The default profile can't be deleted. Make another profile the default first.");

        var requestCount = await db.MediaRequests.CountAsync(r => r.QualityProfileId == id);
        if (requestCount > 0) return (false, $"{requestCount} request(s) still use this profile.");
        var ruleCount = await db.ProfileAssignmentRules.CountAsync(r => r.QualityProfileId == id);
        if (ruleCount > 0) return (false, $"{ruleCount} assignment rule(s) still point at this profile.");

        db.QualityProfiles.Remove(p);
        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> SetDefaultProfileAsync(int id)
    {
        var all = await db.QualityProfiles.ToListAsync();
        if (all.All(p => p.Id != id)) return false;
        foreach (var p in all) p.IsDefault = p.Id == id;
        await db.SaveChangesAsync();
        return true;
    }

    // ---- Assignment rules ---------------------------------------------------------------------------

    public async Task<List<ProfileAssignmentRuleDto>> GetAssignmentRulesAsync()
    {
        var rules = await db.ProfileAssignmentRules.AsNoTracking()
            .OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        var names = await db.QualityProfiles.AsNoTracking().ToDictionaryAsync(p => p.Id, p => p.Name);
        return rules.Select(r => new ProfileAssignmentRuleDto
        {
            Id = r.Id, Name = r.Name, Order = r.Order, Enabled = r.Enabled,
            MatchMediaType = r.MatchMediaType, MatchAnime = r.MatchAnime, MatchGenre = r.MatchGenre,
            MatchTmdbId = r.MatchTmdbId, MatchLibrary = r.MatchLibrary,
            QualityProfileId = r.QualityProfileId,
            QualityProfileName = names.TryGetValue(r.QualityProfileId, out var n) ? n : "(missing)"
        }).ToList();
    }

    public async Task<ProfileAssignmentRuleDto> CreateAssignmentRuleAsync()
    {
        var maxOrder = await db.ProfileAssignmentRules.MaxAsync(r => (int?)r.Order) ?? 0;
        var defaultProfileId = await db.QualityProfiles.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefaultAsync();
        var e = new ProfileAssignmentRuleEntity
        {
            Name = "New rule",
            Order = maxOrder + 1,
            // Disabled on creation, deliberately. The panel this replaces created rules ENABLED with no
            // conditions, which meant one click silently applied that rule's quality to every title in the
            // system — the root cause of a request downloading at the wrong quality.
            Enabled = false,
            QualityProfileId = defaultProfileId
        };
        db.ProfileAssignmentRules.Add(e);
        await db.SaveChangesAsync();
        return new ProfileAssignmentRuleDto
        {
            Id = e.Id, Name = e.Name, Order = e.Order, Enabled = false, QualityProfileId = e.QualityProfileId
        };
    }

    public async Task<(bool ok, string? error)> UpdateAssignmentRuleAsync(ProfileAssignmentRuleDto dto)
    {
        var r = await db.ProfileAssignmentRules.FirstOrDefaultAsync(x => x.Id == dto.Id);
        if (r is null) return (false, "Rule not found.");

        r.Name = string.IsNullOrWhiteSpace(dto.Name) ? r.Name : dto.Name.Trim();
        r.MatchMediaType = dto.MatchMediaType;
        r.MatchAnime = dto.MatchAnime;
        r.MatchGenre = string.IsNullOrWhiteSpace(dto.MatchGenre) ? null : dto.MatchGenre.Trim();
        r.MatchTmdbId = dto.MatchTmdbId;
        r.MatchLibrary = string.IsNullOrWhiteSpace(dto.MatchLibrary) ? null : dto.MatchLibrary.Trim();
        r.QualityProfileId = dto.QualityProfileId;

        // Guard the footgun at the source: a rule with no conditions matches every title, so enabling one
        // silently overrides the default profile for the whole system.
        if (dto.Enabled && r.IsUnconditional)
            return (false, "This rule has no conditions, so it would apply to every title. Add a condition (media type, genre, title or library) before enabling it.");
        r.Enabled = dto.Enabled;

        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> DeleteAssignmentRuleAsync(int id)
    {
        var r = await db.ProfileAssignmentRules.FirstOrDefaultAsync(x => x.Id == id);
        if (r is null) return false;
        db.ProfileAssignmentRules.Remove(r);
        await db.SaveChangesAsync();
        return true;
    }

    public async Task<bool> MoveAssignmentRuleAsync(int id, bool up)
    {
        var ordered = await db.ProfileAssignmentRules.OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        var idx = ordered.FindIndex(r => r.Id == id);
        if (idx < 0) return false;
        var swap = up ? idx - 1 : idx + 1;
        if (swap < 0 || swap >= ordered.Count) return false;
        (ordered[idx].Order, ordered[swap].Order) = (ordered[swap].Order, ordered[idx].Order);
        await db.SaveChangesAsync();
        return true;
    }

    // ---- Resolution ---------------------------------------------------------------------------------

    public async Task<int> ResolveProfileIdAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres,
        int? requesterChoiceId, int? userId, string? library = null, bool? isAnime = null)
    {
        var defaultId = await db.QualityProfiles.Where(p => p.IsDefault).Select(p => p.Id).FirstOrDefaultAsync();
        if (defaultId == 0)
            defaultId = await db.QualityProfiles.OrderBy(p => p.SortOrder).Select(p => p.Id).FirstOrDefaultAsync();

        // 1) The requester's pick, but only if they're genuinely entitled to it. Validating here rather than
        //    trusting the caller means a crafted request can't select a profile the user was never offered.
        if (requesterChoiceId is int choice && choice > 0)
        {
            var selectable = await GetSelectableProfilesAsync(userId, mediaType);
            if (selectable.Any(p => p.Id == choice)) return choice;
            logger.LogInformation("Ignoring quality profile #{Choice} requested by user #{User}: not selectable for them", choice, userId);
        }

        // 2) First matching assignment rule.
        var genreList = genres as IReadOnlyCollection<string> ?? genres?.ToArray() ?? Array.Empty<string>();
        var rules = await db.ProfileAssignmentRules.AsNoTracking().Where(r => r.Enabled)
            .OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        foreach (var rule in rules)
            if (QualityProfileSeeder.Matches(rule, mediaType, tmdbId, genreList, library, isAnime))
                return rule.QualityProfileId;

        // 3) Default.
        return defaultId;
    }

    public async Task<Quality> GetCutoffQualityAsync(int profileId)
    {
        var cutoffDefId = await db.QualityProfiles.Where(p => p.Id == profileId)
            .Select(p => p.CutoffQualityDefinitionId).FirstOrDefaultAsync();
        if (cutoffDefId == 0) return Quality.Any;
        var resolution = await db.QualityDefinitions.Where(d => d.Id == cutoffDefId)
            .Select(d => d.Resolution).FirstOrDefaultAsync();
        return resolution == 0 ? Quality.Any : (Quality)resolution;
    }

    // ---- Helpers ------------------------------------------------------------------------------------

    private static QualityProfileDto ToDto(QualityProfileEntity p, bool inUse) => new()
    {
        Id = p.Id, Name = p.Name, IsDefault = p.IsDefault, IsUserSelectable = p.IsUserSelectable,
        AppliesToMediaTypes = p.AppliesToMediaTypes, SortOrder = p.SortOrder,
        CutoffQualityDefinitionId = p.CutoffQualityDefinitionId, UpgradeAllowed = p.UpgradeAllowed,
        MinCustomFormatScore = p.MinCustomFormatScore, CutoffFormatScore = p.CutoffFormatScore,
        MinSizeGb = p.MinSizeGb, MaxSizeGb = p.MaxSizeGb, MaxSeasonPackSizeGb = p.MaxSeasonPackSizeGb,
        MinSeeders = p.MinSeeders, AllowedLanguagesCsv = p.AllowedLanguagesCsv,
        RequiredAudioLanguagesCsv = p.RequiredAudioLanguagesCsv,
        RequiredSubtitleLanguagesCsv = p.RequiredSubtitleLanguagesCsv,
        RequireForcedSubtitle = p.RequireForcedSubtitle,
        AllowUnknownTrackLanguage = p.AllowUnknownTrackLanguage,
        LanguagePreference = p.LanguagePreference,
        PreferredAudioLanguage = p.PreferredAudioLanguage,
        PreferredSubtitleLanguage = p.PreferredSubtitleLanguage,
        PreferForcedSubtitles = p.PreferForcedSubtitles,
        SetPreferredTracksAsDefault = p.SetPreferredTracksAsDefault,
        InUse = inUse,
        Items = JsonSerializer.Deserialize<List<QualityProfileItemDto>>(p.ItemsJson, Json) ?? new()
    };

    private static bool IsAllowedTier(string itemsJson, int definitionId)
    {
        var items = JsonSerializer.Deserialize<List<QualityProfileItemDto>>(itemsJson, Json) ?? new();
        return items.Any(i => i.Allowed &&
            (i.Members?.Contains(definitionId) == true || i.K == $"q:{definitionId}"));
    }

    private static List<int> ParseIds(string csv) =>
        csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => int.TryParse(s, out var n) ? n : 0).Where(n => n > 0).ToList();

    private static MediaTypeFlags ToFlag(MediaType t) => t switch
    {
        MediaType.Movie => MediaTypeFlags.Movie,
        MediaType.TvShow => MediaTypeFlags.TvShow,
        MediaType.Music => MediaTypeFlags.Music,
        MediaType.Anime => MediaTypeFlags.Anime,
        _ => MediaTypeFlags.All
    };
}
