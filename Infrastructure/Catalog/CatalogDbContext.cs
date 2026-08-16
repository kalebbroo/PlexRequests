using Microsoft.EntityFrameworkCore;

namespace PlexRequestsHosted.Infrastructure.Catalog;

public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    public DbSet<CatalogReleaseEntity> Releases => Set<CatalogReleaseEntity>();
    public DbSet<CatalogSightingEntity> Sightings => Set<CatalogSightingEntity>();
    public DbSet<CatalogCheckpointEntity> Checkpoints => Set<CatalogCheckpointEntity>();
    public DbSet<CatalogBatchReceiptEntity> BatchReceipts => Set<CatalogBatchReceiptEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<CatalogReleaseEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.InfoHash).IsUnique();
            b.HasIndex(x => x.NormalizedTitle);
            b.HasIndex(x => x.LastSeenAt);
            b.HasIndex(x => new { x.Season, x.Episode });
        });

        modelBuilder.Entity<CatalogSightingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.IndexerId, x.ExternalId }).IsUnique();
            b.HasIndex(x => x.LastSeenAt);
            b.HasIndex(x => x.HydrationState);
            b.HasOne(x => x.Release).WithMany(x => x.Sightings)
                .HasForeignKey(x => x.ReleaseId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CatalogCheckpointEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IndexerId).IsUnique();
            b.HasIndex(x => x.NextAttemptAt);
        });

        modelBuilder.Entity<CatalogBatchReceiptEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.IndexerId, x.BatchId }).IsUnique();
            b.HasIndex(x => x.CommittedAt);
        });
    }
}
