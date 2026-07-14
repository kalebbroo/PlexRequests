namespace PlexRequestsHosted.Shared;

/// <summary>
/// Single shared "is this anime" heuristic — Genre=Animation plus a Japanese origin signal (original
/// language or production/origin country), the same signal combination the Sonarr/Radarr-family tools
/// use since TMDB has no genre literally called "Anime" (everything animated, Japanese or Western, is
/// tagged "Animation"). Pure function so both the web app and the downloader worker can call the exact
/// same logic without depending on each other or duplicating it.
/// </summary>
public static class AnimeClassifier
{
    public static bool IsAnime(IEnumerable<string>? genres, IEnumerable<string>? languages, IEnumerable<string>? countries)
    {
        var genreSet = genres as ICollection<string> ?? genres?.ToList() ?? new List<string>();
        if (!genreSet.Any(g => string.Equals(g, "Animation", StringComparison.OrdinalIgnoreCase)))
            return false;

        var isJapaneseLanguage = languages?.Any(l => string.Equals(l, "ja", StringComparison.OrdinalIgnoreCase)) ?? false;
        var isJapaneseOrigin = countries?.Any(c =>
            string.Equals(c, "JP", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(c, "Japan", StringComparison.OrdinalIgnoreCase)) ?? false;

        return isJapaneseLanguage || isJapaneseOrigin;
    }
}
