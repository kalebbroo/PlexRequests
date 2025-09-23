using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MediaRequestEntity> MediaRequests => Set<MediaRequestEntity>();
    public DbSet<WatchlistItemEntity> Watchlist => Set<WatchlistItemEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaRequestEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(512);
            b.HasIndex(x => new { x.MediaId, x.MediaType });
        });

        modelBuilder.Entity<WatchlistItemEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.MediaId).IsUnique(false);
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
    }
}
