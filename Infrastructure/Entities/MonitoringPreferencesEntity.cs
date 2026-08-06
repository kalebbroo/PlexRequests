using System.ComponentModel.DataAnnotations;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Admin knobs for ongoing-series monitoring. A single row, matching the pattern used for download and
/// library preferences.
///
/// The air-time settings exist because TMDB reports an air DATE and no time, so "shortly after it airs"
/// has to be assumed rather than known. Making the assumption configurable is better than hardcoding a
/// guess that's wrong for anyone outside one timezone.
/// </summary>
public class MonitoringPreferencesEntity
{
    [Key] public int Id { get; set; }
    public bool IsSingleton { get; set; } = true;

    /// <summary>Assumed local broadcast time, "HH:mm". Most scripted TV airs in the evening.</summary>
    [MaxLength(8)] public string AssumedAirTimeLocal { get; set; } = "20:00";

    /// <summary>Timezone that assumed time is in.</summary>
    [MaxLength(64)] public string AirTimeZoneId { get; set; } = "America/New_York";

    /// <summary>How long after the assumed air time to start looking. Releases don't appear instantly.</summary>
    public int PostAirDelayMinutes { get; set; } = 90;

    /// <summary>How often the downloader sweeps indexer RSS for anything wanted. This is the real safety
    /// net for the air-time estimate being off.</summary>
    public int RssSweepIntervalSeconds { get; set; } = 900;

    /// <summary>Give up searching one episode after this many attempts; it stays visible in the calendar.</summary>
    public int MaxSearchAttemptsPerEpisode { get; set; } = 12;

    /// <summary>How far ahead the calendar is populated, and how far back to backfill on first run.</summary>
    public int CalendarHorizonDays { get; set; } = 90;
    public int CalendarBackfillDays { get; set; } = 14;

    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
