using PlexRequests.Downloader.Configuration;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequests.Downloader.Organize;

/// <summary>Builds Plex-convention destination paths from the admin-configured naming templates.</summary>
public interface IPlexNamingService
{
    /// <summary>Full destination path for a movie file.</summary>
    string BuildMoviePath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, string ext);

    /// <summary>Full destination path for a single TV episode file.</summary>
    string BuildEpisodePath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, int season, int episode, string? episodeTitle, string ext);

    /// <summary>Destination folder for a season pack that isn't being split into per-episode files.</summary>
    string BuildSeasonPackFolder(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, int season);

    /// <summary>Full destination path for one music track.</summary>
    string BuildMusicTrackPath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job,
        string artist, string album, int disc, int track, string trackTitle, string ext);
}

public class PlexNamingService : IPlexNamingService
{
    public string BuildMoviePath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, string ext)
    {
        var (root, template) = prefs.Resolve(job, MediaType.Movie, isEpisode: false);
        var ctx = new TemplateContext(Title: job.Title, Year: job.Year, Quality: QualityLabel(job.Quality), Ext: NormalizeExt(ext));
        return Combine(root, NamingTemplateEngine.Render(template, ctx));
    }

    public string BuildEpisodePath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, int season, int episode, string? episodeTitle, string ext)
    {
        var (root, template) = prefs.Resolve(job, MediaType.TvShow, isEpisode: true);
        var ctx = new TemplateContext(
            Title: job.Title, ShowTitle: job.Title, Year: job.Year, Season: season, Episode: episode,
            EpisodeTitle: episodeTitle, Quality: QualityLabel(job.Quality), Ext: NormalizeExt(ext));
        return Combine(root, NamingTemplateEngine.Render(template, ctx));
    }

    public string BuildSeasonPackFolder(EffectiveLibraryOrganization prefs, FulfillmentJobDto job, int season)
    {
        var (root, template) = prefs.Resolve(job, MediaType.TvShow, isEpisode: false);
        var ctx = new TemplateContext(Title: job.Title, ShowTitle: job.Title, Year: job.Year, Season: season);
        return Combine(root, NamingTemplateEngine.Render(template, ctx));
    }

    public string BuildMusicTrackPath(EffectiveLibraryOrganization prefs, FulfillmentJobDto job,
        string artist, string album, int disc, int track, string trackTitle, string ext)
    {
        var (root, template) = prefs.Resolve(job, MediaType.Music, isEpisode: false);
        var ctx = new TemplateContext(Title: job.Title, Year: job.Year, Artist: artist, Album: album,
            Disc: disc, Track: track, TrackTitle: trackTitle, Ext: NormalizeExt(ext));
        return Combine(root, NamingTemplateEngine.Render(template, ctx));
    }

    private static string NormalizeExt(string ext) => ext.StartsWith('.') ? ext : "." + ext;

    private static string QualityLabel(Quality q) => q switch
    {
        Quality.SD => "480p",
        Quality.HD => "720p",
        Quality.FullHD => "1080p",
        Quality.UHD4K => "2160p",
        Quality.UHD8K => "4320p",
        _ => string.Empty
    };

    // Combine a configured root with a rendered relative path whose segments may use either slash style
    // (templates are authored as literal strings, not Path.Combine calls) — normalize to the host's
    // separator so the result is a single well-formed path.
    private static string Combine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(root))
            throw new InvalidOperationException("The selected library destination has no root path.");
        var normalized = relative.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        var canonicalRoot = Path.GetFullPath(root);
        var destination = Path.GetFullPath(Path.Combine(canonicalRoot, normalized));
        var rootPrefix = canonicalRoot.EndsWith(Path.DirectorySeparatorChar)
            ? canonicalRoot
            : canonicalRoot + Path.DirectorySeparatorChar;
        if (!destination.StartsWith(rootPrefix, StringComparison.Ordinal)
            && !string.Equals(destination, canonicalRoot, StringComparison.Ordinal))
            throw new InvalidOperationException("The rendered library path escapes its configured destination root.");
        return destination;
    }
}
