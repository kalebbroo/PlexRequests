using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MediaRequestEntity> MediaRequests => Set<MediaRequestEntity>();
    public DbSet<WatchlistItemEntity> Watchlist => Set<WatchlistItemEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();

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
    }
}
