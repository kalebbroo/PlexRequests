using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public interface ILibraryOrganizationPreferencesService
{
    /// <summary>Get the global library-organization preferences, lazily creating the default row if none exists.</summary>
    Task<LibraryOrganizationPreferencesDto> GetAsync();
    /// <summary>Persist edited preferences over the single settings row.</summary>
    Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs);
}

/// <summary>
/// DB-backed store for the single global <see cref="LibraryOrganizationPreferencesEntity"/> row,
/// mirroring <see cref="DownloadPreferencesService"/>'s singleton pattern.
/// </summary>
public class LibraryOrganizationPreferencesService(AppDbContext db) : ILibraryOrganizationPreferencesService
{
    public async Task<LibraryOrganizationPreferencesDto> GetAsync() => ToDto(await GetOrCreateAsync());

    public async Task<bool> UpdateAsync(LibraryOrganizationPreferencesDto prefs)
    {
        var e = await GetOrCreateAsync();
        e.MoviePath = prefs.MoviePath?.Trim() ?? string.Empty;
        e.TvPath = prefs.TvPath?.Trim() ?? string.Empty;
        e.MovieTemplate = string.IsNullOrWhiteSpace(prefs.MovieTemplate) ? e.MovieTemplate : prefs.MovieTemplate.Trim();
        e.TvEpisodeTemplate = string.IsNullOrWhiteSpace(prefs.TvEpisodeTemplate) ? e.TvEpisodeTemplate : prefs.TvEpisodeTemplate.Trim();
        e.SeasonPackFolderTemplate = string.IsNullOrWhiteSpace(prefs.SeasonPackFolderTemplate) ? e.SeasonPackFolderTemplate : prefs.SeasonPackFolderTemplate.Trim();
        e.LibraryRootRulesJson = prefs.LibraryRootRules is { Count: > 0 } ? JsonSerializer.Serialize(prefs.LibraryRootRules) : null;
        e.TransferMode = prefs.TransferMode;
        e.ExtractArchives = prefs.ExtractArchives;
        e.SplitSeasonPacks = prefs.SplitSeasonPacks;
        e.KeepSubtitles = prefs.KeepSubtitles;
        e.SubtitleExtensionsCsv = string.IsNullOrWhiteSpace(prefs.SubtitleExtensionsCsv) ? e.SubtitleExtensionsCsv : prefs.SubtitleExtensionsCsv.Trim();
        e.VideoExtensionsCsv = string.IsNullOrWhiteSpace(prefs.VideoExtensionsCsv) ? e.VideoExtensionsCsv : prefs.VideoExtensionsCsv.Trim();
        e.MinVideoFileSizeMb = Math.Max(0, prefs.MinVideoFileSizeMb);
        // Deleting the source only makes sense when the library copy isn't the same inode as the source.
        e.DeleteSourceAfterImport = prefs.DeleteSourceAfterImport && prefs.TransferMode != Shared.Enums.TransferMode.Hardlink;
        e.UpdatedAt = DateTime.UtcNow;
        return await db.SaveChangesAsync() > 0;
    }

    private async Task<LibraryOrganizationPreferencesEntity> GetOrCreateAsync()
    {
        var e = await db.LibraryOrganizationPreferences.FirstOrDefaultAsync(x => x.IsSingleton);
        if (e is not null) return e;
        e = new LibraryOrganizationPreferencesEntity { IsSingleton = true };
        db.LibraryOrganizationPreferences.Add(e);
        await db.SaveChangesAsync();
        return e;
    }

    private static LibraryOrganizationPreferencesDto ToDto(LibraryOrganizationPreferencesEntity e) => new()
    {
        MoviePath = e.MoviePath,
        TvPath = e.TvPath,
        MovieTemplate = e.MovieTemplate,
        TvEpisodeTemplate = e.TvEpisodeTemplate,
        SeasonPackFolderTemplate = e.SeasonPackFolderTemplate,
        LibraryRootRules = string.IsNullOrWhiteSpace(e.LibraryRootRulesJson)
            ? new List<LibraryRootRuleDto>()
            : (JsonSerializer.Deserialize<List<LibraryRootRuleDto>>(e.LibraryRootRulesJson) ?? new List<LibraryRootRuleDto>()),
        TransferMode = e.TransferMode,
        ExtractArchives = e.ExtractArchives,
        SplitSeasonPacks = e.SplitSeasonPacks,
        KeepSubtitles = e.KeepSubtitles,
        SubtitleExtensionsCsv = e.SubtitleExtensionsCsv,
        VideoExtensionsCsv = e.VideoExtensionsCsv,
        MinVideoFileSizeMb = e.MinVideoFileSizeMb,
        DeleteSourceAfterImport = e.DeleteSourceAfterImport
    };
}
