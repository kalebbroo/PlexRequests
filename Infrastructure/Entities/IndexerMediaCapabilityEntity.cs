using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// A media capability row for an indexer. This replaces the expanding set of SupportsX/CategoriesX columns;
/// adding a new media module now adds data, not schema and switch branches.
/// </summary>
public class IndexerMediaCapabilityEntity
{
    public int Id { get; set; }
    public int IndexerId { get; set; }
    public MediaType MediaType { get; set; }
    public bool Enabled { get; set; }
    [MaxLength(256)] public string? CategoriesCsv { get; set; }

    public IndexerEntity? Indexer { get; set; }
}
