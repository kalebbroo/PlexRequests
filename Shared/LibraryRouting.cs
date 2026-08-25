using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>Shared, deterministic library routing used by both the web enqueue boundary and legacy workers.
/// The web embeds the returned snapshot into a job; the downloader only re-evaluates settings for jobs that
/// predate snapshots.</summary>
public static class LibraryRouting
{
    public const string DefaultMovieId = "default-movies";
    public const string DefaultSeriesId = "default-tv";
    public const string DefaultMusicId = "default-music";

    public static LibraryContentKind ContentKind(MediaType mediaType) => mediaType switch
    {
        MediaType.Movie => LibraryContentKind.Movie,
        MediaType.Music => LibraryContentKind.Music,
        MediaType.TvShow or MediaType.Anime => LibraryContentKind.Series,
        _ => throw new ArgumentOutOfRangeException(nameof(mediaType), mediaType, null)
    };

    /// <summary>Upgrade the old three-path + inline-rule contract in memory. Ids are deterministic, so web
    /// and downloader agree during rolling deployment even before an admin saves the new form.</summary>
    public static void EnsureDestinationModel(LibraryOrganizationPreferencesDto prefs)
    {
        prefs.LibraryDestinations ??= new List<LibraryDestinationDto>();
        prefs.LibraryRootRules ??= new List<LibraryRootRuleDto>();

        EnsureDefault(prefs, LibraryContentKind.Movie, DefaultMovieId, "Movies", prefs.MoviePath);
        EnsureDefault(prefs, LibraryContentKind.Series, DefaultSeriesId, "TV Shows", prefs.TvPath);
        EnsureDefault(prefs, LibraryContentKind.Music, DefaultMusicId, "Music", prefs.MusicPath);

        for (var i = 0; i < prefs.LibraryRootRules.Count; i++)
        {
            var rule = prefs.LibraryRootRules[i];
            if (!string.IsNullOrWhiteSpace(rule.DestinationId) || string.IsNullOrWhiteSpace(rule.RootPath))
                continue;

            var kind = ContentKind(rule.MediaType);
            var existing = prefs.LibraryDestinations.FirstOrDefault(d => d.ContentKind == kind
                && string.Equals(d.RootPath.Trim(), rule.RootPath.Trim(), StringComparison.Ordinal)
                && string.Equals(d.TemplateOverride?.Trim(), rule.TemplateOverride?.Trim(), StringComparison.Ordinal));
            if (existing is null)
            {
                var baseId = $"migrated-route-{i + 1}";
                var id = baseId;
                var suffix = 2;
                while (prefs.LibraryDestinations.Any(d => string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase)))
                    id = $"{baseId}-{suffix++}";
                existing = new LibraryDestinationDto
                {
                    Id = id,
                    Name = $"Imported {KindLabel(kind)} route {i + 1}",
                    ContentKind = kind,
                    RootPath = rule.RootPath.Trim(),
                    TemplateOverride = NullIfWhiteSpace(rule.TemplateOverride),
                    Enabled = true
                };
                prefs.LibraryDestinations.Add(existing);
            }
            rule.DestinationId = existing.Id;
        }

        NormalizeDefaults(prefs, LibraryContentKind.Movie);
        NormalizeDefaults(prefs, LibraryContentKind.Series);
        NormalizeDefaults(prefs, LibraryContentKind.Music);
        SyncLegacyRoots(prefs);
    }

    public static bool TryNormalizeAndValidate(LibraryOrganizationPreferencesDto prefs, out string? error)
    {
        EnsureDestinationModel(prefs);
        foreach (var destination in prefs.LibraryDestinations)
        {
            destination.Id = destination.Id?.Trim() ?? string.Empty;
            destination.Name = destination.Name?.Trim() ?? string.Empty;
            destination.RootPath = destination.RootPath?.Trim() ?? string.Empty;
            destination.PlexSectionId = NullIfWhiteSpace(destination.PlexSectionId);
            destination.TemplateOverride = NullIfWhiteSpace(destination.TemplateOverride);
            if (string.IsNullOrWhiteSpace(destination.Id))
            {
                error = "Every library destination needs a stable id.";
                return false;
            }
            if (string.IsNullOrWhiteSpace(destination.Name))
            {
                error = $"Library destination '{destination.Id}' needs a name.";
                return false;
            }
            if (destination.Enabled && string.IsNullOrWhiteSpace(destination.RootPath))
            {
                error = $"Library destination '{destination.Name}' needs a root path before it can be enabled.";
                return false;
            }
        }

        var duplicate = prefs.LibraryDestinations.GroupBy(d => d.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(g => g.Count() > 1);
        if (duplicate is not null)
        {
            error = $"Library destination id '{duplicate.Key}' is duplicated.";
            return false;
        }

        foreach (var kind in Enum.GetValues<LibraryContentKind>())
        {
            var defaults = prefs.LibraryDestinations.Where(d => d.ContentKind == kind && d.IsDefault && d.Enabled).ToList();
            if (defaults.Count != 1)
            {
                error = $"Choose exactly one enabled default {KindLabel(kind)} destination.";
                return false;
            }
        }

        foreach (var rule in prefs.LibraryRootRules)
        {
            rule.DestinationId = NullIfWhiteSpace(rule.DestinationId);
            rule.GenreContains = NullIfWhiteSpace(rule.GenreContains);
            rule.TemplateOverride = NullIfWhiteSpace(rule.TemplateOverride);
            if (rule.DestinationId is null)
            {
                if (string.IsNullOrWhiteSpace(rule.RootPath))
                {
                    error = "Every routing rule must select a destination.";
                    return false;
                }
                continue;
            }

            var destination = prefs.LibraryDestinations.FirstOrDefault(d =>
                string.Equals(d.Id, rule.DestinationId, StringComparison.OrdinalIgnoreCase));
            if (destination is null || !destination.Enabled)
            {
                error = $"Routing rule references missing or disabled destination '{rule.DestinationId}'.";
                return false;
            }
            if (destination.ContentKind != ContentKind(rule.MediaType))
            {
                error = $"Routing rule for {rule.MediaType} cannot target the {destination.ContentKind} destination '{destination.Name}'.";
                return false;
            }
        }

        SyncLegacyRoots(prefs);
        error = null;
        return true;
    }

    public static LibraryDestinationSnapshotDto Resolve(
        LibraryOrganizationPreferencesDto prefs,
        MediaType mediaType,
        Quality? quality,
        IReadOnlyList<string>? genres,
        bool isAnime,
        bool isEpisode,
        string? preferredDestinationId = null)
    {
        EnsureDestinationModel(prefs);
        var kind = ContentKind(mediaType);

        if (!string.IsNullOrWhiteSpace(preferredDestinationId))
            return Snapshot(RequireDestination(prefs, preferredDestinationId, kind), prefs, isEpisode, null);

        foreach (var rule in prefs.LibraryRootRules)
        {
            if (ContentKind(rule.MediaType) != kind) continue;
            // Older configurations could use MediaType.Anime as their classifier. Anime is now a durable
            // property of a Movie/TV request, but those legacy rules must retain their narrower meaning.
            if (rule.MediaType == MediaType.Anime && !isAnime) continue;
            if (rule.MinQuality is Quality minimum && (quality is null || (int)quality.Value < (int)minimum)) continue;
            if (rule.RequireAnime is bool requireAnime && requireAnime != isAnime) continue;
            if (!string.IsNullOrWhiteSpace(rule.GenreContains)
                && (genres is null || !genres.Any(g => g.Contains(rule.GenreContains, StringComparison.OrdinalIgnoreCase))))
                continue;

            if (!string.IsNullOrWhiteSpace(rule.DestinationId))
                return Snapshot(RequireDestination(prefs, rule.DestinationId, kind), prefs, isEpisode,
                    rule.TemplateOverride);

            // Rolling-upgrade fallback for a rule serialized by an old web version.
            if (!string.IsNullOrWhiteSpace(rule.RootPath))
                return new LibraryDestinationSnapshotDto
                {
                    Id = "legacy-inline",
                    Name = "Legacy inline route",
                    ContentKind = kind,
                    RootPath = rule.RootPath.Trim(),
                    Template = NullIfWhiteSpace(rule.TemplateOverride) ?? DefaultTemplate(prefs, kind, isEpisode)
                };
            throw new InvalidOperationException("A matching library routing rule has no destination.");
        }

        var fallback = prefs.LibraryDestinations.FirstOrDefault(d =>
            d.ContentKind == kind && d.IsDefault && d.Enabled);
        if (fallback is null)
            throw new InvalidOperationException($"No enabled default {KindLabel(kind)} library destination is configured.");
        return Snapshot(fallback, prefs, isEpisode, null);
    }

    private static LibraryDestinationDto RequireDestination(LibraryOrganizationPreferencesDto prefs, string id,
        LibraryContentKind kind)
    {
        var destination = prefs.LibraryDestinations.FirstOrDefault(d =>
            string.Equals(d.Id, id, StringComparison.OrdinalIgnoreCase));
        if (destination is null || !destination.Enabled)
            throw new InvalidOperationException($"Library destination '{id}' is missing or disabled.");
        if (destination.ContentKind != kind)
            throw new InvalidOperationException(
                $"Library destination '{destination.Name}' accepts {destination.ContentKind}, not {kind}.");
        if (string.IsNullOrWhiteSpace(destination.RootPath))
            throw new InvalidOperationException($"Library destination '{destination.Name}' has no root path.");
        return destination;
    }

    private static LibraryDestinationSnapshotDto Snapshot(LibraryDestinationDto destination,
        LibraryOrganizationPreferencesDto prefs, bool isEpisode, string? ruleTemplate)
    {
        var templateOverride = NullIfWhiteSpace(ruleTemplate) ?? NullIfWhiteSpace(destination.TemplateOverride);
        return new LibraryDestinationSnapshotDto
        {
            Id = destination.Id,
            Name = destination.Name,
            ContentKind = destination.ContentKind,
            RootPath = destination.RootPath.Trim(),
            PlexSectionId = NullIfWhiteSpace(destination.PlexSectionId),
            Template = templateOverride ?? DefaultTemplate(prefs, destination.ContentKind, isEpisode),
            SeasonPackFolderTemplate = destination.ContentKind == LibraryContentKind.Series
                ? templateOverride ?? prefs.SeasonPackFolderTemplate
                : null
        };
    }

    private static string DefaultTemplate(LibraryOrganizationPreferencesDto prefs, LibraryContentKind kind,
        bool isEpisode) => kind switch
    {
        LibraryContentKind.Movie => prefs.MovieTemplate,
        LibraryContentKind.Music => prefs.MusicTrackTemplate,
        _ => isEpisode ? prefs.TvEpisodeTemplate : prefs.SeasonPackFolderTemplate
    };

    private static void EnsureDefault(LibraryOrganizationPreferencesDto prefs, LibraryContentKind kind,
        string id, string name, string root)
    {
        if (prefs.LibraryDestinations.Any(d => d.ContentKind == kind)) return;
        prefs.LibraryDestinations.Add(new LibraryDestinationDto
        {
            Id = id,
            Name = name,
            ContentKind = kind,
            RootPath = root?.Trim() ?? string.Empty,
            IsDefault = true,
            Enabled = true
        });
    }

    private static void NormalizeDefaults(LibraryOrganizationPreferencesDto prefs, LibraryContentKind kind)
    {
        var candidates = prefs.LibraryDestinations.Where(d => d.ContentKind == kind).ToList();
        if (candidates.Count == 0) return;
        var selected = candidates.FirstOrDefault(d => d.IsDefault && d.Enabled)
                       ?? candidates.FirstOrDefault(d => d.Enabled)
                       ?? candidates[0];
        foreach (var candidate in candidates) candidate.IsDefault = ReferenceEquals(candidate, selected);
    }

    private static void SyncLegacyRoots(LibraryOrganizationPreferencesDto prefs)
    {
        prefs.MoviePath = DefaultRoot(prefs, LibraryContentKind.Movie, prefs.MoviePath);
        prefs.TvPath = DefaultRoot(prefs, LibraryContentKind.Series, prefs.TvPath);
        prefs.MusicPath = DefaultRoot(prefs, LibraryContentKind.Music, prefs.MusicPath);
    }

    private static string DefaultRoot(LibraryOrganizationPreferencesDto prefs, LibraryContentKind kind,
        string fallback) => prefs.LibraryDestinations.FirstOrDefault(d => d.ContentKind == kind && d.IsDefault)
            ?.RootPath?.Trim() ?? fallback?.Trim() ?? string.Empty;

    private static string KindLabel(LibraryContentKind kind) => kind switch
    {
        LibraryContentKind.Movie => "movie",
        LibraryContentKind.Series => "series",
        _ => "music"
    };

    private static string? NullIfWhiteSpace(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
