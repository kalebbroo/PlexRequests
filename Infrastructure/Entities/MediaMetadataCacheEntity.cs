using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// Persistent provider-neutral metadata cache. Legacy TMDb rows retain their integer lookup; all new
/// consumers attach the row to a canonical <see cref="MediaIdentityEntity"/> so UUID/string providers
/// such as MusicBrainz are cached without synthetic ids. Denormalized fields serve lightweight lists,
/// while <see cref="DetailJson"/> holds the full detail DTO for stale-while-revalidate reads.
/// </summary>
public class MediaMetadataCacheEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    public MediaType MediaType { get; set; }   // part of the unique key
    public int TmdbId { get; set; }            // part of the unique key
    public int? MediaIdentityId { get; set; }
    public MediaIdentityEntity? MediaIdentity { get; set; }

    [MaxLength(32)] public string? ImdbId { get; set; }   // folds in GetImdbIdAsync

    // ---- denormalized card fields (a MediaCardDto without deserializing DetailJson) ----
    [MaxLength(512)] public string Title { get; set; } = string.Empty;
    public string? Overview { get; set; }
    public string? PosterUrl { get; set; }
    public string? BackdropUrl { get; set; }
    public int? Year { get; set; }
    public decimal? Rating { get; set; }
    public int? Runtime { get; set; }
    [MaxLength(1024)] public string GenresCsv { get; set; } = string.Empty;
    public int TotalSeasons { get; set; }
    [MaxLength(64)] public string? Status { get; set; }   // "Returning Series" etc. — drives refresh TTL

    // ---- full detail blob (null until a real detail fetch has populated it) ----
    public string? DetailJson { get; set; }

    public DateTime CardFetchedAt { get; set; }       // any card/detail write
    public DateTime? DetailFetchedAt { get; set; }    // only when DetailJson is populated
}
