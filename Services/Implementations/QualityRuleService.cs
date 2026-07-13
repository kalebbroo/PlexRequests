using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Services.Implementations;

public interface IQualityRuleService
{
    Task<List<QualityRuleDto>> GetRulesAsync();
    Task<QualityRuleDto> CreateRuleAsync(QualityRuleDto rule);
    Task<bool> UpdateRuleAsync(QualityRuleDto rule);
    Task<bool> DeleteRuleAsync(int id);
    Task<bool> MoveRuleAsync(int id, bool up);
    /// <summary>Resolve the effective quality for a title: first matching enabled override, else the default.</summary>
    Task<Quality> ResolveQualityAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres, string? library = null);
}

public class QualityRuleService(AppDbContext db) : IQualityRuleService
{
    public async Task<List<QualityRuleDto>> GetRulesAsync()
    {
        await EnsureDefaultAsync();
        return await db.QualityRules.AsNoTracking().OrderBy(r => r.IsDefault).ThenBy(r => r.Order).ThenBy(r => r.Id)
            .Select(r => ToDto(r)).ToListAsync();
    }

    public async Task<Quality> ResolveQualityAsync(MediaType mediaType, int tmdbId, IEnumerable<string>? genres, string? library = null)
    {
        var genreSet = new HashSet<string>(genres ?? Enumerable.Empty<string>(), StringComparer.OrdinalIgnoreCase);
        var rules = await db.QualityRules.AsNoTracking().Where(r => r.Enabled).OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();

        foreach (var r in rules.Where(r => !r.IsDefault))
        {
            if (r.MatchMediaType.HasValue && r.MatchMediaType.Value != mediaType) continue;
            if (r.MatchTmdbId.HasValue && r.MatchTmdbId.Value != tmdbId) continue;
            if (!string.IsNullOrWhiteSpace(r.MatchGenre) && !genreSet.Contains(r.MatchGenre)) continue;
            if (!string.IsNullOrWhiteSpace(r.MatchLibrary) && !string.Equals(r.MatchLibrary, library, StringComparison.OrdinalIgnoreCase)) continue;
            return r.TargetQuality;   // first match wins
        }

        var def = rules.FirstOrDefault(r => r.IsDefault) ?? await GetOrCreateDefaultAsync();
        return def.TargetQuality;
    }

    public async Task<QualityRuleDto> CreateRuleAsync(QualityRuleDto rule)
    {
        var maxOrder = await db.QualityRules.Where(r => !r.IsDefault).MaxAsync(r => (int?)r.Order) ?? 0;
        var e = new QualityRuleEntity
        {
            Name = string.IsNullOrWhiteSpace(rule.Name) ? "New rule" : rule.Name,
            Order = maxOrder + 1,
            Enabled = rule.Enabled,
            IsDefault = false,
            MatchMediaType = rule.MatchMediaType,
            MatchGenre = string.IsNullOrWhiteSpace(rule.MatchGenre) ? null : rule.MatchGenre.Trim(),
            MatchTmdbId = rule.MatchTmdbId,
            MatchLibrary = string.IsNullOrWhiteSpace(rule.MatchLibrary) ? null : rule.MatchLibrary.Trim(),
            TargetQuality = rule.TargetQuality
        };
        db.QualityRules.Add(e);
        await db.SaveChangesAsync();
        return ToDto(e);
    }

    public async Task<bool> UpdateRuleAsync(QualityRuleDto rule)
    {
        var e = await db.QualityRules.FirstOrDefaultAsync(r => r.Id == rule.Id);
        if (e is null) return false;
        e.Name = rule.Name;
        e.Enabled = rule.Enabled;
        e.TargetQuality = rule.TargetQuality;
        if (!e.IsDefault)   // the default rule only carries a quality, no conditions
        {
            e.MatchMediaType = rule.MatchMediaType;
            e.MatchGenre = string.IsNullOrWhiteSpace(rule.MatchGenre) ? null : rule.MatchGenre.Trim();
            e.MatchTmdbId = rule.MatchTmdbId;
            e.MatchLibrary = string.IsNullOrWhiteSpace(rule.MatchLibrary) ? null : rule.MatchLibrary.Trim();
        }
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> DeleteRuleAsync(int id)
    {
        var e = await db.QualityRules.FirstOrDefaultAsync(r => r.Id == id);
        if (e is null || e.IsDefault) return false;   // never delete the default
        db.QualityRules.Remove(e);
        return await db.SaveChangesAsync() > 0;
    }

    public async Task<bool> MoveRuleAsync(int id, bool up)
    {
        var ordered = await db.QualityRules.Where(r => !r.IsDefault).OrderBy(r => r.Order).ThenBy(r => r.Id).ToListAsync();
        var idx = ordered.FindIndex(r => r.Id == id);
        if (idx < 0) return false;
        var swap = up ? idx - 1 : idx + 1;
        if (swap < 0 || swap >= ordered.Count) return false;
        (ordered[idx].Order, ordered[swap].Order) = (ordered[swap].Order, ordered[idx].Order);
        return await db.SaveChangesAsync() > 0;
    }

    private async Task EnsureDefaultAsync()
    {
        if (!await db.QualityRules.AnyAsync(r => r.IsDefault)) await GetOrCreateDefaultAsync();
    }

    private async Task<QualityRuleEntity> GetOrCreateDefaultAsync()
    {
        var def = await db.QualityRules.FirstOrDefaultAsync(r => r.IsDefault);
        if (def is not null) return def;
        def = new QualityRuleEntity { Name = "Default", IsDefault = true, Order = int.MaxValue, TargetQuality = Quality.FullHD };
        db.QualityRules.Add(def);
        await db.SaveChangesAsync();
        return def;
    }

    private static QualityRuleDto ToDto(QualityRuleEntity r) => new()
    {
        Id = r.Id, Name = r.Name, Order = r.Order, Enabled = r.Enabled, IsDefault = r.IsDefault,
        MatchMediaType = r.MatchMediaType, MatchGenre = r.MatchGenre, MatchTmdbId = r.MatchTmdbId,
        MatchLibrary = r.MatchLibrary, TargetQuality = r.TargetQuality
    };
}
