using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

public interface ICustomFormatService
{
    Task<List<CustomFormatDto>> GetAllAsync();
    /// <summary>Formats with their score in one profile, for the profile's scoring grid.</summary>
    Task<List<CustomFormatDto>> GetForProfileAsync(int profileId);
    Task<(bool ok, string? error)> SaveAsync(CustomFormatDto dto);
    Task<(bool ok, string? error)> DeleteAsync(int id);
    Task<bool> SetScoreAsync(int profileId, int formatId, int score);
    /// <summary>Format id → score for one profile, as the ranking context needs it.</summary>
    Task<Dictionary<int, int>> ScoresForProfileAsync(int profileId);
    Task SeedAsync();
}

/// <summary>
/// CRUD over custom formats plus their per-profile scores.
///
/// Scores deliberately live on the (profile, format) pair rather than on the format: "x265" is worth
/// having in a space-conscious 1080p profile and irrelevant in a remux one, and the same rule shouldn't
/// need duplicating to express that.
/// </summary>
public class CustomFormatService(AppDbContext db, ILogger<CustomFormatService> logger) : ICustomFormatService
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<List<CustomFormatDto>> GetAllAsync()
    {
        await SeedAsync();
        var rows = await db.CustomFormats.AsNoTracking().OrderBy(f => f.Name).ToListAsync();
        return rows.Select(ToDto).ToList();
    }

    public async Task<List<CustomFormatDto>> GetForProfileAsync(int profileId)
    {
        var formats = await GetAllAsync();
        var scores = await ScoresForProfileAsync(profileId);
        foreach (var f in formats) f.Score = scores.TryGetValue(f.Id, out var s) ? s : 0;
        return formats;
    }

    public async Task<Dictionary<int, int>> ScoresForProfileAsync(int profileId) =>
        await db.CustomFormatScores.AsNoTracking()
            .Where(s => s.QualityProfileId == profileId)
            .ToDictionaryAsync(s => s.CustomFormatId, s => s.Score);

    public async Task<(bool ok, string? error)> SaveAsync(CustomFormatDto dto)
    {
        var name = (dto.Name ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(name)) return (false, "A name is required.");
        if (await db.CustomFormats.AnyAsync(f => f.Name == name && f.Id != dto.Id))
            return (false, $"A format named \"{name}\" already exists.");
        if (dto.Specifications.Count == 0) return (false, "Add at least one condition.");

        // Validate every regex here rather than letting a bad one reach the downloader's ranking loop,
        // where it would either never match or, worse, backtrack catastrophically on every candidate.
        foreach (var spec in dto.Specifications.Where(s => s.Op == FormatOp.Regex))
            if (!CustomFormatMatcher.IsValidPattern(spec.Value, out var error))
                return (false, $"{spec.Field}: {error}");

        var row = dto.Id == 0 ? new CustomFormatEntity() : await db.CustomFormats.FirstOrDefaultAsync(f => f.Id == dto.Id);
        if (row is null) return (false, "Format not found.");

        row.Name = name;
        row.Enabled = dto.Enabled;
        row.SpecificationsJson = JsonSerializer.Serialize(dto.Specifications, Json);
        row.UpdatedAt = DateTime.UtcNow;
        if (dto.Id == 0) { row.CreatedAt = DateTime.UtcNow; db.CustomFormats.Add(row); }

        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<(bool ok, string? error)> DeleteAsync(int id)
    {
        var row = await db.CustomFormats.FirstOrDefaultAsync(f => f.Id == id);
        if (row is null) return (false, "Format not found.");
        if (row.IsSystem) return (false, "Built-in formats can't be deleted — disable it or set its score to 0.");
        db.CustomFormats.Remove(row);
        await db.SaveChangesAsync();
        return (true, null);
    }

    public async Task<bool> SetScoreAsync(int profileId, int formatId, int score)
    {
        var row = await db.CustomFormatScores
            .FirstOrDefaultAsync(s => s.QualityProfileId == profileId && s.CustomFormatId == formatId);
        if (row is null)
        {
            row = new CustomFormatScoreEntity { QualityProfileId = profileId, CustomFormatId = formatId };
            db.CustomFormatScores.Add(row);
        }
        row.Score = Math.Clamp(score, -100000, 100000);
        await db.SaveChangesAsync();
        return true;
    }

    // ---- Seeding ------------------------------------------------------------------------------------

    /// <summary>
    /// Stock formats, so the feature is useful without anyone writing a regex first — and so the spec model
    /// has worked examples to copy.
    /// </summary>
    private static readonly (string Name, FormatSpecificationDto[] Specs)[] SystemFormats =
    {
        ("x265", new[] { Spec(FormatField.VideoCodec, FormatOp.Equals, "x265") }),
        ("AV1", new[] { Spec(FormatField.VideoCodec, FormatOp.Equals, "av1") }),
        ("Dolby Vision", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\b(dv|dovi|dolby[\s._-]*vision)\b") }),
        ("HDR10+", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"hdr10\+") }),
        ("Atmos", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\batmos\b") }),
        ("Lossless audio", new[]
        {
            Spec(FormatField.AudioCodec, FormatOp.Equals, "DTS-HD MA"),
            Spec(FormatField.AudioCodec, FormatOp.Equals, "TrueHD"),
            Spec(FormatField.AudioCodec, FormatOp.Equals, "FLAC")
        }),
        ("Surround 5.1+", new[]
        {
            Spec(FormatField.AudioChannels, FormatOp.Equals, "5.1"),
            Spec(FormatField.AudioChannels, FormatOp.Equals, "7.1")
        }),
        ("Repack/Proper", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\b(repack|proper)\b") }),
        ("Remux", new[] { Spec(FormatField.Source, FormatOp.Equals, "Remux") }),
        // Negative by convention — an admin gives these a negative score.
        ("Upscaled", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\b(upscal(e|ed)|ai[\s._-]*upscale)\b") }),
        ("Multi-language", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\b(multi|dual)\b") }),
        ("Extras only", new[] { Spec(FormatField.ReleaseTitle, FormatOp.Regex, @"\b(extras|bonus[\s._-]*disc|featurettes)\b") })
    };

    private static FormatSpecificationDto Spec(FormatField field, FormatOp op, string value) =>
        new() { Field = field, Op = op, Value = value };

    public async Task SeedAsync()
    {
        var existing = await db.CustomFormats.Select(f => f.Name).ToListAsync();
        int added = 0;
        foreach (var (name, specs) in SystemFormats.Where(f => !existing.Contains(f.Name)))
        {
            db.CustomFormats.Add(new CustomFormatEntity
            {
                Name = name,
                Enabled = true,
                IsSystem = true,
                SpecificationsJson = JsonSerializer.Serialize(specs, Json)
            });
            added++;
        }
        if (added == 0) return;
        try { await db.SaveChangesAsync(); logger.LogInformation("Seeded {Count} built-in custom format(s)", added); }
        catch (DbUpdateException)
        {
            foreach (var e in db.ChangeTracker.Entries<CustomFormatEntity>().Where(e => e.State == EntityState.Added).ToList())
                e.State = EntityState.Detached;
        }
    }

    private static CustomFormatDto ToDto(CustomFormatEntity f) => new()
    {
        Id = f.Id,
        Name = f.Name,
        Enabled = f.Enabled,
        IsSystem = f.IsSystem,
        Specifications = JsonSerializer.Deserialize<List<FormatSpecificationDto>>(f.SpecificationsJson, Json) ?? new()
    };
}
