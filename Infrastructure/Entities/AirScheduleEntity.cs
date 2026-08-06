using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// One episode's air date and the search state that follows from it. This is both the calendar the admin
/// sees and the wake queue the scheduler drives off — keeping them as one table means they can't disagree.
///
/// It replaces blind polling. Previously the monitor woke every six hours and asked every monitored show
/// whether anything had aired, which is both wasteful and slow: an episode airing just after a pass would
/// wait most of a cycle. Here the job asks "when is the next thing due?" and sleeps until then.
/// </summary>
public class AirScheduleEntity
{
    public int Id { get; set; }

    public int ShowTmdbId { get; set; }
    public int SeasonNumber { get; set; }
    public int EpisodeNumber { get; set; }
    [MaxLength(256)] public string? EpisodeTitle { get; set; }

    /// <summary>The date TMDB reports. Date-only — TMDB carries no air TIME.</summary>
    public DateTime? AirDate { get; set; }

    /// <summary>
    /// When to start looking, in UTC: the air date plus an assumed local air time plus a post-air delay.
    /// Necessarily an estimate, because the metadata source gives a date and nothing finer. The RSS sweep
    /// is the safety net that makes the imprecision tolerable.
    /// </summary>
    public DateTime? AirsAtUtc { get; set; }

    /// <summary>The monitoring anchor request this episode belongs to.</summary>
    public int? MediaRequestId { get; set; }

    public bool Monitored { get; set; } = true;
    /// <summary>Already on Plex.</summary>
    public bool HasFile { get; set; }

    public AirSearchState SearchState { get; set; } = AirSearchState.NotDue;

    /// <summary>When the monitor should next consider this row. The scheduler's wake time is the minimum
    /// of these across all pending rows.</summary>
    public DateTime? NextSearchAt { get; set; }
    public DateTime? LastSearchedAt { get; set; }
    public int SearchAttempts { get; set; }

    [MaxLength(16)] public string SourceProvider { get; set; } = "tmdb";
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
