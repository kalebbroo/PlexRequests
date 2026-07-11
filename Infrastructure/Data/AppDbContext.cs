using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MediaRequestEntity> MediaRequests => Set<MediaRequestEntity>();
    public DbSet<WatchlistItemEntity> Watchlist => Set<WatchlistItemEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<PlexMappingEntity> PlexMappings => Set<PlexMappingEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<FulfillmentJobEntity> FulfillmentJobs => Set<FulfillmentJobEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaRequestEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(512);
            b.HasIndex(x => new { x.MediaId, x.MediaType });
            b.HasIndex(x => x.RequestedBy); // Index for querying by username
            b.HasIndex(x => x.RequestedByUserId); // Index for querying by user ID
            b.HasIndex(x => x.Status); // Index for filtering by status
            b.HasIndex(x => x.RequestedAt); // Index for sorting by date

            b.HasOne(x => x.RequestedByUser)
                .WithMany()
                .HasForeignKey(x => x.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<WatchlistItemEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.MediaId).IsUnique(false);
            b.HasIndex(x => new { x.UserId, x.MediaId, x.MediaType }); // Composite index for queries
            b.HasIndex(x => x.Username); // Index for username queries (backward compat)

            b.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<UserEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Username).IsUnique();
        });

        modelBuilder.Entity<UserProfileEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.UserId).IsUnique();
            b.HasIndex(x => x.PlexUsername);
            b.HasOne(x => x.User)
                .WithOne()
                .HasForeignKey<UserProfileEntity>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<PlexMappingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ExternalKey).IsUnique();
            b.HasIndex(x => x.RatingKey);
        });

        modelBuilder.Entity<NotificationEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.UserId);
            b.HasIndex(x => new { x.UserId, x.IsRead }); // For fetching unread notifications
            b.HasIndex(x => x.CreatedAt); // For sorting by date

            b.HasOne(x => x.User)
                .WithMany()
                .HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            b.HasOne(x => x.RelatedRequest)
                .WithMany()
                .HasForeignKey(x => x.RelatedRequestId)
                .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<FulfillmentJobEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(512);
            b.HasIndex(x => x.Status); // worker polls queued jobs
            b.HasIndex(x => x.MediaRequestId);

            b.HasOne(x => x.MediaRequest)
                .WithMany()
                .HasForeignKey(x => x.MediaRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
