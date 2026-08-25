using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PlexRequests.Downloader.Api;
using PlexRequestsHosted.Shared;
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
    public string MusicPath { get; init; } = string.Empty;
    public string MovieTemplate { get; init; } = "{Title} ({Year})/{Title} ({Year}){Ext}";
    public string TvEpisodeTemplate { get; init; } = "{ShowTitle} ({Year})/Season {Season:00}/{ShowTitle} - s{Season:00}e{Episode:00} - {EpisodeTitle}{Ext}";
    public string SeasonPackFolderTemplate { get; init; } = "{ShowTitle} ({Year})/Season {Season:00}";
    public string MusicTrackTemplate { get; init; } = "{Artist}/{Album} ({Year})/{Disc:00}-{Track:00} - {TrackTitle}{Ext}";
    public IReadOnlyList<LibraryRootRuleDto> RootRules { get; init; } = Array.Empty<LibraryRootRuleDto>();
    public IReadOnlyList<LibraryDestinationDto> Destinations { get; init; } = Array.Empty<LibraryDestinationDto>();
    public TransferMode TransferMode { get; init; } = TransferMode.Hardlink;
    public bool ExtractArchives { get; init; } = true;
    public bool SplitSeasonPacks { get; init; } = true;
    public bool KeepSubtitles { get; init; } = true;
    public string[] SubtitleExtensions { get; init; } = { ".srt", ".ass", ".ssa", ".sub", ".vtt" };
    public string[] VideoExtensions { get; init; } = { ".mkv", ".mp4", ".avi", ".m4v", ".ts", ".mov", ".wmv", ".m2ts" };
    public string[] AudioExtensions { get; init; } = { ".flac", ".mp3", ".m4a", ".aac", ".ogg", ".opus", ".wav", ".wma", ".alac" };
    public double MinVideoFileSizeMb { get; init; } = 50;
    public double MinAudioFileSizeMb { get; init; } = 1;
    public bool DeleteSourceAfterImport { get; init; } = false;

    /// <summary>Resolve a legacy job that predates immutable destination snapshots.</summary>
    public (string Root, string Template) Resolve(MediaType mediaType, Quality? quality, IReadOnlyList<string>? genres, bool isAnime, bool isEpisode)
    {
        var dto = new LibraryOrganizationPreferencesDto
        {
            MoviePath = MoviePath,
            TvPath = TvPath,
            MusicPath = MusicPath,
            MovieTemplate = MovieTemplate,
            TvEpisodeTemplate = TvEpisodeTemplate,
            SeasonPackFolderTemplate = SeasonPackFolderTemplate,
            MusicTrackTemplate = MusicTrackTemplate,
            LibraryRootRules = RootRules.ToList(),
            LibraryDestinations = Destinations.ToList()
        };
        var resolved = LibraryRouting.Resolve(dto, mediaType, quality, genres, isAnime, isEpisode);
        return (resolved.RootPath, resolved.Template);
    }

    /// <summary>Use the job's immutable destination when present; validate its scanner shape before any
    /// path is built. Falling back is only for jobs created by an older web version.</summary>
    public (string Root, string Template) Resolve(FulfillmentJobDto job, MediaType mediaType, bool isEpisode)
    {
        if (job.LibraryDestination is not null)
        {
            var expected = LibraryRouting.ContentKind(mediaType);
            if (job.LibraryDestination.ContentKind != expected)
                throw new InvalidOperationException(
                    $"Library destination '{job.LibraryDestination.Name}' accepts {job.LibraryDestination.ContentKind}, not {expected}.");
            if (string.IsNullOrWhiteSpace(job.LibraryDestination.RootPath)
                || string.IsNullOrWhiteSpace(job.LibraryDestination.Template))
                throw new InvalidOperationException("The job's library destination snapshot is incomplete.");
            var template = !isEpisode && expected == LibraryContentKind.Series
                ? job.LibraryDestination.SeasonPackFolderTemplate ?? job.LibraryDestination.Template
                : job.LibraryDestination.Template;
            return (job.LibraryDestination.RootPath, template);
        }
        return Resolve(mediaType, job.Quality, job.Genres, job.IsAnime, isEpisode);
    }
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
            MusicPath = l.MusicPath,
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
        MusicPath = d.MusicPath,
        MovieTemplate = d.MovieTemplate,
        TvEpisodeTemplate = d.TvEpisodeTemplate,
        SeasonPackFolderTemplate = d.SeasonPackFolderTemplate,
        MusicTrackTemplate = d.MusicTrackTemplate,
        RootRules = d.LibraryRootRules ?? new List<LibraryRootRuleDto>(),
        Destinations = d.LibraryDestinations ?? new List<LibraryDestinationDto>(),
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
        AudioExtensions = string.IsNullOrWhiteSpace(d.AudioExtensionsCsv)
            ? Array.Empty<string>()
            : d.AudioExtensionsCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
        MinVideoFileSizeMb = d.MinVideoFileSizeMb,
        MinAudioFileSizeMb = d.MinAudioFileSizeMb,
        DeleteSourceAfterImport = d.DeleteSourceAfterImport
    };
}
