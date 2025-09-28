using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

public class PlexMappingEntity
{
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public int Id { get; set; }

    // External key format: "tmdb:803796", "imdb:tt12345", "tvdb:344262"
    [Required]
    [MaxLength(64)]
    public string ExternalKey { get; set; } = string.Empty;

    // Plex ratingKey for the item
    [Required]
    [MaxLength(64)]
    public string RatingKey { get; set; } = string.Empty;

    public MediaType? MediaType { get; set; }

    [MaxLength(512)]
    public string? Title { get; set; }

    public int? Year { get; set; }

    public DateTime LastSeenAt { get; set; } = DateTime.UtcNow;
}
