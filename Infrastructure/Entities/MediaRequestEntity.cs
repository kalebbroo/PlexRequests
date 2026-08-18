using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class MediaRequestEntity
{
    public int Id { get; set; }
    public int MediaId { get; set; }   // TMDb id for movie/TV; 0 for sources without an int id (music)
    public MediaType MediaType { get; set; }

    // Provider-agnostic identifier for non-TMDb sources (e.g. MusicBrainz MBID, Plex ratingKey, TVDB id).
    // TODO(music): music requests key off this instead of MediaId. Thread it through fulfillment + the
    // availability match so an album/artist request can be found and downloaded.
    [MaxLength(128)]
    public string? ExternalId { get; set; }

    [MaxLength(32)]
    public string? ExternalSource { get; set; }   // "musicbrainz" | "plex" | "tvdb" | ... (null = tmdb)

    [MaxLength(512)]
    public string Title { get; set; } = string.Empty;

    [MaxLength(512)]
    public string? PosterUrl { get; set; }

    public RequestStatus Status { get; set; } = RequestStatus.Pending;
    public DateTime RequestedAt { get; set; } = DateTime.UtcNow;

    // Foreign key to UserEntity
    public int? RequestedByUserId { get; set; }

    // Keep for backward compatibility, but should migrate to RequestedByUserId
    [MaxLength(128)]
    public string? RequestedBy { get; set; }
    public DateTime? ApprovedAt { get; set; }
    public DateTime? AvailableAt { get; set; }

    [MaxLength(1000)]
    public string? DenialReason { get; set; }

    [MaxLength(2000)]
    public string? RequestNote { get; set; }

    // TV specific selection
    public bool RequestAllSeasons { get; set; }

    [MaxLength(256)]
    public string? RequestedSeasonsCsv { get; set; }

    // Episode-level selection, format "S1E1,S1E2,S2E5". Null/empty ⇒ season/series level applies.
    [MaxLength(4096)]
    public string? RequestedEpisodesCsv { get; set; }

    // --- Ongoing-series monitoring -------------------------------------------------------------------
    /// <summary>
    /// Which episodes to keep chasing. This is the authoritative field; <see cref="Monitored"/> is kept as
    /// a shadow of "not None" so existing queries keep working during the transition.
    /// </summary>
    public MonitorMode MonitorMode { get; set; } = MonitorMode.None;

    /// <summary>Legacy mirror of <c>MonitorMode != None</c>. Read <see cref="MonitorMode"/> instead.</summary>
    public bool Monitored { get; set; }

    /// <summary>Pick up seasons that appear after the request was made — the difference between "the show
    /// I asked for" and "this show, ongoing".</summary>
    public bool AutoMonitorNewSeasons { get; set; } = true;

    /// <summary>Keep looking for a better release once something is in the library.</summary>
    public bool SearchForCutoffUpgrades { get; set; } = true;

    public DateTime? MonitoredSince { get; set; }
    /// <summary>Last time the calendar was refreshed for this series.</summary>
    public DateTime? LastMonitorCheckAt { get; set; }

    /// <summary>Per-series override of the assumed air time, in minutes from midnight local. Null uses the
    /// global default.</summary>
    public int? AirTimeOverrideMinutes { get; set; }

    /// <summary>
    /// For the one reusable per-season request created by the episode monitor, identifies the durable
    /// whole-series/season request whose coverage it represents. Null for user-created requests. Keeping
    /// this identity on the request prevents every reconciliation pass from appending another child row.
    /// </summary>
    public int? MonitoringAnchorId { get; set; }

    /// <summary>The season represented by this monitor child. Unique together with MonitoringAnchorId.</summary>
    public int? MonitoringSeasonNumber { get; set; }

    // --- Achieved quality / upgrade tracking ---------------------------------------------------------
    /// <summary>The lowest quality tier actually imported into the library for this request (min across its
    /// video files). <see cref="Quality.Any"/> until something imports. Compared against the preferred
    /// target to decide whether the request's cutoff is met.</summary>
    public Quality AchievedQuality { get; set; } = Quality.Any;

    /// <summary>
    /// The quality profile governing this request. Every creation path now resolves one up front
    /// (requester's pick, validated -> assignment rule -> default profile) and the startup seeder backfills
    /// any row that predates profiles, so in practice this is never null — it is what the ranker, the
    /// upgrade scan and the cutoff check all read to decide what "good enough" means for this title.
    ///
    /// The column stays NULLABLE deliberately. Making it required means a SQLite table rebuild plus a
    /// restricting FK, and on a deployment where the profiles migration and that one land in the SAME
    /// startup pass, QualityProfiles is still empty when the constraint is applied: every row would take
    /// the default 0, the FK would find no parent, and the app would fail to start. The constraint is only
    /// safe on a deploy where profiles are already live, so it's a separate decision, not a side effect.
    /// </summary>
    public int? QualityProfileId { get; set; }

    /// <summary>The lowest quality tier actually imported, as a <see cref="QualityDefinitionEntity"/> id.
    /// The finer-grained counterpart to <see cref="AchievedQuality"/>, which only tracks resolution.</summary>
    public int? AchievedQualityDefinitionId { get; set; }

    /// <summary>Aggregate custom-format score of what's imported. Inert until custom formats ship.</summary>
    public int AchievedFormatScore { get; set; }
    /// <summary>Whether the imported files meet the preferred quality, or whether that's simply not knowable
    /// yet. This is the authoritative field; see <see cref="Shared.Enums.CutoffState"/> for why it isn't a
    /// boolean. Only <see cref="Shared.Enums.CutoffState.Unmet"/> makes a request an upgrade candidate.</summary>
    public CutoffState CutoffState { get; set; } = CutoffState.Unknown;

    /// <summary>Legacy mirror of <see cref="CutoffState"/> (<c>state == Met</c>), kept for one release so a
    /// rollback doesn't lose the column. Nothing should read this — read <see cref="CutoffState"/>.</summary>
    public bool CutoffMet { get; set; } = true;
    /// <summary>When the upgrade scanner last enqueued (or considered) an upgrade for this request — gates the
    /// per-request upgrade cooldown so it isn't re-searched every scan.</summary>
    public DateTime? LastUpgradeSearchAt { get; set; }
    /// <summary>How many upgrade searches have run for this request (bounds churn; shown in the admin UI).</summary>
    public int UpgradeAttempts { get; set; }

    // Navigation property
    [ForeignKey(nameof(RequestedByUserId))]
    public UserEntity? RequestedByUser { get; set; }
}
