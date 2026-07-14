using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Configuration;

/// <summary>
/// Effective library-organization settings the organizer reads: where completed downloads go, how
/// they're named, and how the transfer happens. Sourced from the web app's admin config
/// (<see cref="LibraryOrganizationPreferencesDto"/>) with the local <see cref="LibraryOptions"/>
/// appsettings as the offline fallback, mirroring <see cref="IDownloadPreferencesProvider"/>'s pattern.
/// </summary>
public class EffectiveLibraryOrganization
{
    public string MoviePath { get; init; } = string.Empty;
    public string TvPath { get; init; } = string.Empty;
    public string MovieTemplate { get; init; } = "{Title} ({Year})/{Title} ({Year}){Ext}";
    public string TvEpisodeTemplate { get; init; } = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}";
    public string SeasonPackFolderTemplate { get; init; } = "{ShowTitle} ({Year})/Season {Season:00}";
    public IReadOnlyList<LibraryRootRuleDto> RootRules { get; init; } = Array.Empty<LibraryRootRuleDto>();
    public TransferMode TransferMode { get; init; } = TransferMode.Hardlink;
    public bool ExtractArchives { get; init; } = true;
    public bool SplitSeasonPacks { get; init; } = true;
    public bool KeepSubtitles { get; init; } = true;
    public string[] SubtitleExtensions { get; init; } = { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
    public string[] VideoExtensions { get; init; } = { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".m2ts" };
    public double MinVideoFileSizeMb { get; init; } = 50;
    public bool DeleteSourceAfterImport { get; init; } = false;

    /// <summary>Resolve the effective (root, template) for a job: first matching routing rule wins,
    /// else the media type's default root/template.</summary>
    public (string Root, string Template) Resolve(MediaType mediaType, Quality? quality, IReadOnlyList<string>? genres, bool isEpisode)
    {
        foreach (var rule in RootRules)
        {
            if (rule.MediaType != mediaType) continue;
            if (rule.MinQuality is Quality mq && quality is Quality q && (int)q < (int)mq) continue;
            if (!string.IsNullOrWhiteSpace(rule.GenreContains) &&
                (genres is null || !genres.Any(g => g.Contains(rule.GenreContains, StringComparison.OrdinalIgnoreCase))))
                continue;

            var template = !string.IsNullOrWhiteSpace(rule.TemplateOverride)
                ? rule.TemplateOverride!
                : DefaultTemplate(mediaType, isEpisode);
            return (rule.RootPath, template);
        }

        var defaultRoot = mediaType == MediaType.Movie ? MoviePath : TvPath;
        return (defaultRoot, DefaultTemplate(mediaType, isEpisode));
    }

    private string DefaultTemplate(MediaType mediaType, bool isEpisode) =>
        mediaType == MediaType.Movie ? MovieTemplate : (isEpisode ? TvEpisodeTemplate : SeasonPackFolderTemplate);
}

public interface ILibraryOrganizationProvider
{
    /// <summary>The last successfully fetched preferences, or the appsettings fallback until the first fetch.</summary>
    EffectiveLibraryOrganization Current { get; }
    /// <summary>Fetch the latest admin config, throttled to at most once per refresh interval.</summary>
    Task RefreshAsync(CancellationToken ct);
}

public class LibraryOrganizationPreferencesProvider : ILibraryOrganizationProvider
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(2);

    private readonly IServiceProvider _services;
    private readonly ILogger<LibraryOrganizationPreferencesProvider> _logger;
    private readonly EffectiveLibraryOrganization _fallback;
    private readonly SemaphoreSlim _lock = new(1, 1);
    private volatile EffectiveLibraryOrganization _current;
    private DateTime _lastFetch = DateTime.MinValue;

    public LibraryOrganizationPreferencesProvider(
        IServiceProvider services,
        IOptions<LibraryOptions> library,
        ILogger<LibraryOrganizationPreferencesProvider> logger)
    {
        _services = services;
        _logger = logger;
        var l = library.Value;
        // Seed defaults from the local appsettings paths/hardlink flag; these apply until (and if) the
        // web config loads. Naming templates have no appsettings equivalent — Plex-standard defaults.
        _fallback = new EffectiveLibraryOrganization
        {
            MoviePath = l.MoviePath,
            TvPath = l.TvPath,
            TransferMode = l.Hardlink ? TransferMode.Hardlink : TransferMode.Move
        };
        _current = _fallback;
    }

    public EffectiveLibraryOrganization Current => _current;

    public async Task RefreshAsync(CancellationToken ct)
    {
        if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
        if (!await _lock.WaitAsync(0, ct)) return;
        try
        {
            if (DateTime.UtcNow - _lastFetch < RefreshInterval) return;
            using var scope = _services.CreateScope();
            var api = scope.ServiceProvider.GetRequiredService<IPlexRequestsApiClient>();
            var dto = await api.GetLibraryConfigAsync(ct);
            if (dto is not null)
            {
                _current = Map(dto);
                _lastFetch = DateTime.UtcNow;
                _logger.LogInformation("Library organization preferences refreshed (transferMode={Mode}, splitPacks={Split})",
                    _current.TransferMode, _current.SplitSeasonPacks);
            }
            else
            {
                _lastFetch = DateTime.UtcNow; // keep last-good/fallback; retry next interval
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Could not refresh library organization preferences; using last-known values");
        }
        finally { _lock.Release(); }
    }

    private static EffectiveLibraryOrganization Map(LibraryOrganizationPreferencesDto d) => new()
    {
        MoviePath = d.MoviePath,
        TvPath = d.TvPath,
        MovieTemplate = d.MovieTemplate,
        TvEpisodeTemplate = d.TvEpisodeTemplate,
        SeasonPackFolderTemplate = d.SeasonPackFolderTemplate,
        RootRules = d.LibraryRootRules,
        TransferMode = d.TransferMode,
        ExtractArchives = d.ExtractArchives,
        SplitSeasonPacks = d.SplitSeasonPacks,
        KeepSubtitles = d.KeepSubtitles,
        SubtitleExtensions = string.IsNullOrWhiteSpace(d.SubtitleExtensionsCsv)
            ? Array.Empty<string>()
            : d.SubtitleExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        VideoExtensions = string.IsNullOrWhiteSpace(d.VideoExtensionsCsv)
            ? Array.Empty<string>()
            : d.VideoExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MinVideoFileSizeMb = d.MinVideoFileSizeMb,
        DeleteSourceAfterImport = d.DeleteSourceAfterImport
    };
}
