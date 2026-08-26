using Microsoft.EntityFrameworkCore;
using PlexRequestsHosted.Infrastructure.Entities;

namespace PlexRequestsHosted.Infrastructure.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MediaRequestEntity> MediaRequests => Set<MediaRequestEntity>();
    public DbSet<MediaIdentityEntity> MediaIdentities => Set<MediaIdentityEntity>();
    public DbSet<MediaExternalIdentifierEntity> MediaExternalIdentifiers => Set<MediaExternalIdentifierEntity>();
    public DbSet<WatchlistItemEntity> Watchlist => Set<WatchlistItemEntity>();
    public DbSet<UserEntity> Users => Set<UserEntity>();
    public DbSet<UserProfileEntity> UserProfiles => Set<UserProfileEntity>();
    public DbSet<UserGroupEntity> UserGroups => Set<UserGroupEntity>();
    public DbSet<UserAccessAuditEntity> UserAccessAudits => Set<UserAccessAuditEntity>();
    public DbSet<PlexMappingEntity> PlexMappings => Set<PlexMappingEntity>();
    public DbSet<PlexSeasonAvailabilityEntity> PlexSeasonAvailability => Set<PlexSeasonAvailabilityEntity>();
    public DbSet<MediaMetadataCacheEntity> MediaMetadataCache => Set<MediaMetadataCacheEntity>();
    public DbSet<MusicSettingsEntity> MusicSettings => Set<MusicSettingsEntity>();
    public DbSet<SeasonEpisodesCacheEntity> SeasonEpisodesCache => Set<SeasonEpisodesCacheEntity>();
    public DbSet<MediaIssueEntity> MediaIssues => Set<MediaIssueEntity>();
    public DbSet<QualityRuleEntity> QualityRules => Set<QualityRuleEntity>();
    public DbSet<QualityDefinitionEntity> QualityDefinitions => Set<QualityDefinitionEntity>();
    public DbSet<QualityProfileEntity> QualityProfiles => Set<QualityProfileEntity>();
    public DbSet<ProfileAssignmentRuleEntity> ProfileAssignmentRules => Set<ProfileAssignmentRuleEntity>();
    public DbSet<DownloadPreferencesEntity> DownloadPreferences => Set<DownloadPreferencesEntity>();
    public DbSet<LibraryOrganizationPreferencesEntity> LibraryOrganizationPreferences => Set<LibraryOrganizationPreferencesEntity>();
    public DbSet<NetworkShareEntity> NetworkShares => Set<NetworkShareEntity>();
    public DbSet<NotificationEntity> Notifications => Set<NotificationEntity>();
    public DbSet<FulfillmentJobEntity> FulfillmentJobs => Set<FulfillmentJobEntity>();
    public DbSet<ImportedFileEntity> ImportedFiles => Set<ImportedFileEntity>();
    public DbSet<ImportedEpisodeCoverageEntity> ImportedEpisodeCoverage => Set<ImportedEpisodeCoverageEntity>();
    public DbSet<FulfillmentTransferEntity> FulfillmentTransfers => Set<FulfillmentTransferEntity>();
    public DbSet<BridgeOutboxEntity> BridgeOutbox => Set<BridgeOutboxEntity>();
    public DbSet<ScheduledJobEntity> ScheduledJobs => Set<ScheduledJobEntity>();
    public DbSet<JobRunEntity> JobRuns => Set<JobRunEntity>();
    public DbSet<IndexerEntity> Indexers => Set<IndexerEntity>();
    public DbSet<IndexerMediaCapabilityEntity> IndexerMediaCapabilities => Set<IndexerMediaCapabilityEntity>();
    public DbSet<SearchTaskEntity> SearchTasks => Set<SearchTaskEntity>();
    public DbSet<ReleaseBlocklistEntity> ReleaseBlocklist => Set<ReleaseBlocklistEntity>();
    public DbSet<RecommendedItemEntity> RecommendedItems => Set<RecommendedItemEntity>();
    public DbSet<AirScheduleEntity> AirSchedule => Set<AirScheduleEntity>();
    public DbSet<CustomFormatEntity> CustomFormats => Set<CustomFormatEntity>();
    public DbSet<CustomFormatScoreEntity> CustomFormatScores => Set<CustomFormatScoreEntity>();
    public DbSet<MonitoringPreferencesEntity> MonitoringPreferences => Set<MonitoringPreferencesEntity>();
    public DbSet<FirefoxCapturePairingEntity> FirefoxCapturePairings => Set<FirefoxCapturePairingEntity>();
    public DbSet<FirefoxCaptureDeviceEntity> FirefoxCaptureDevices => Set<FirefoxCaptureDeviceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MediaRequestEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(512);
            b.HasIndex(x => new { x.MediaId, x.MediaType });
            // Status-scoped scans are the hot path for the background jobs (reconciliation walks the open
            // statuses, the upgrade scan and series monitor filter Available), and they had no index.
            b.HasIndex(x => new { x.Status, x.MediaType });
            b.HasIndex(x => new { x.Monitored, x.MediaType, x.Status }); // the monitor's anchor scan
            b.HasIndex(x => x.RequestedBy); // Index for querying by username
            b.HasIndex(x => x.RequestedByUserId); // Index for querying by user ID
            b.HasIndex(x => x.Status); // Index for filtering by status
            b.HasIndex(x => x.RequestedAt); // Index for sorting by date

            b.HasOne(x => x.RequestedByUser)
                .WithMany()
                .HasForeignKey(x => x.RequestedByUserId)
                .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(x => x.MediaIdentity)
                .WithMany()
                .HasForeignKey(x => x.MediaIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MediaIdentityEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.StableKey).IsUnique();
            b.HasIndex(x => new { x.MediaType, x.Kind });
        });

        modelBuilder.Entity<MediaExternalIdentifierEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.MediaType, x.Kind, x.Provider, x.ExternalId }).IsUnique();
            b.HasIndex(x => x.MediaIdentityId);
            b.HasOne(x => x.MediaIdentity).WithMany(x => x.ExternalIdentifiers)
                .HasForeignKey(x => x.MediaIdentityId)
                .OnDelete(DeleteBehavior.Cascade);
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
            b.HasOne(x => x.MediaIdentity).WithMany()
                .HasForeignKey(x => x.MediaIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
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
            b.HasIndex(x => x.DiscordUserId).IsUnique().HasFilter("\"DiscordUserId\" IS NOT NULL");
            b.HasOne(x => x.User)
                .WithOne()
                .HasForeignKey<UserProfileEntity>(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.UserGroup)
                .WithMany(x => x.Members)
                .HasForeignKey(x => x.UserGroupId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<UserGroupEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<UserAccessAuditEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.CreatedAt);
            b.HasIndex(x => new { x.TargetType, x.TargetId, x.Id });
            b.HasIndex(x => new { x.ActorUserId, x.Id });
        });

        modelBuilder.Entity<PlexMappingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ExternalKey).IsUnique();
            b.HasIndex(x => x.RatingKey);
        });

        modelBuilder.Entity<PlexSeasonAvailabilityEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.ShowRatingKey);
            b.HasIndex(x => new { x.ShowRatingKey, x.SeasonNumber }).IsUnique();
        });

        modelBuilder.Entity<MediaMetadataCacheEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.MediaType, x.TmdbId }).IsUnique().HasFilter("\"TmdbId\" > 0");
            b.HasIndex(x => x.MediaIdentityId).IsUnique();
            b.HasOne(x => x.MediaIdentity).WithMany()
                .HasForeignKey(x => x.MediaIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<MusicSettingsEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IsSingleton).IsUnique();
        });

        modelBuilder.Entity<SeasonEpisodesCacheEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ShowTmdbId, x.SeasonNumber }).IsUnique();
        });

        modelBuilder.Entity<MediaIssueEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Status);
            b.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<QualityRuleEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Order);
        });

        modelBuilder.Entity<QualityDefinitionEntity>(b =>
        {
            b.HasKey(x => x.Id);
            // One row per tier; the seeder relies on this to stay idempotent.
            b.HasIndex(x => new { x.Resolution, x.Source }).IsUnique();
            b.HasIndex(x => x.SortWeight);
        });

        modelBuilder.Entity<QualityProfileEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();
            b.HasIndex(x => x.IsDefault);
        });

        modelBuilder.Entity<ProfileAssignmentRuleEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Order);
            b.Ignore(x => x.IsUnconditional); // computed guard, not persisted
            // Restrict: a profile that rules still point at must not be deletable out from under them.
            b.HasOne<QualityProfileEntity>().WithMany()
                .HasForeignKey(x => x.QualityProfileId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<FulfillmentTransferEntity>(b =>
        {
            // Keep the deployed table/column names while the code and API become protocol-neutral. This
            // turns the production migration into one additive Protocol column rather than a risky rename.
            b.ToTable("FulfillmentTorrents");
            b.HasKey(x => x.Id);
            // The join key the reconciler uses every cycle, and the uniqueness that makes registering a
            // torrent idempotent — re-adding the same one after a restart updates rather than duplicates.
            b.Property(x => x.TransferId).HasColumnName("TorrentId");
            b.Property(x => x.SourceId).HasColumnName("InfoHash");
            b.HasIndex(x => x.TransferId);
            b.HasIndex(x => new { x.FulfillmentJobId, x.Protocol, x.TransferId }).IsUnique();
            // "Everything still in flight" is the reconciler's hot query.
            b.HasIndex(x => x.State);
            b.HasIndex(x => x.SourceId);
            // A job's torrents are meaningless without the job.
            b.HasOne(x => x.Job).WithMany()
                .HasForeignKey(x => x.FulfillmentJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<DownloadPreferencesEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IsSingleton).IsUnique(); // enforce the single settings row
        });

        modelBuilder.Entity<LibraryOrganizationPreferencesEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IsSingleton).IsUnique(); // enforce the single settings row
        });

        modelBuilder.Entity<NetworkShareEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.MountSlug).IsUnique(); // one mount path per share
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
            // The application guard makes enqueue idempotent; the filtered unique index closes the small
            // race between two monitor/browser workers checking and inserting concurrently. Terminal jobs
            // remain unlimited history, while a request can own only one live unit of work.
            b.HasIndex(x => x.MediaRequestId).IsUnique()
                .HasFilter("\"Status\" IN (0, 1, 2, 7)");
            b.HasIndex(x => new { x.MediaId, x.MediaType }); // cross-request in-flight job dedup
            b.HasIndex(x => x.MediaIdentityId); // provider-neutral cross-request dedup (music/non-TMDb)
            b.HasIndex(x => x.NextRetryAt); // scheduler scans deferred jobs whose backoff has elapsed

            b.HasOne(x => x.MediaRequest)
                .WithMany()
                .HasForeignKey(x => x.MediaRequestId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.MediaIdentity).WithMany()
                .HasForeignKey(x => x.MediaIdentityId)
                .OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportedFileEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.TransferId).HasColumnName("TorrentId");
            b.HasIndex(x => x.FulfillmentJobId);

            b.HasOne(x => x.FulfillmentJob)
                .WithMany()
                .HasForeignKey(x => x.FulfillmentJobId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ImportedEpisodeCoverageEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ImportedFileId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
            b.HasIndex(x => new { x.SeasonNumber, x.EpisodeNumber });
            b.HasOne(x => x.ImportedFile)
                .WithMany(x => x.EpisodeCoverage)
                .HasForeignKey(x => x.ImportedFileId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<BridgeOutboxEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.Property(x => x.Title).HasMaxLength(512);
            b.HasIndex(x => x.Id); // cursor scans (PK already indexed, explicit for clarity)
            b.HasIndex(x => x.DeduplicationKey).IsUnique();
            b.HasIndex(x => new { x.AcknowledgedAt, x.Id });

            b.HasOne(x => x.MediaRequest)
                .WithMany()
                .HasForeignKey(x => x.MediaRequestId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ScheduledJobEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.JobType).IsUnique(); // one schedule row per job type
            b.HasIndex(x => x.NextRunAt);           // scheduler polls due jobs
        });

        modelBuilder.Entity<JobRunEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.JobType);
            b.HasIndex(x => x.StartedAt); // history feed sorts newest-first
        });

        modelBuilder.Entity<SearchTaskEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.Status, x.CreatedAt }); // the worker's claim query
            b.HasIndex(x => x.ExpiresAt);                   // the cleanup job
            b.HasIndex(x => x.MediaRequestId);
        });

        modelBuilder.Entity<ReleaseBlocklistEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.InfoHash);
            b.HasIndex(x => new { x.Protocol, x.SourceId });
            b.HasIndex(x => x.NormalizedReleaseName);
            b.HasIndex(x => new { x.MediaId, x.MediaType });
            // One block per release per request; re-failing the same torrent must not pile up rows.
            b.HasIndex(x => new { x.InfoHash, x.MediaRequestId }).IsUnique();
            b.HasIndex(x => new { x.Protocol, x.SourceId, x.MediaRequestId }).IsUnique();
        });

        modelBuilder.Entity<CustomFormatEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();
        });

        modelBuilder.Entity<CustomFormatScoreEntity>(b =>
        {
            b.HasKey(x => x.Id);
            // One score per format per profile.
            b.HasIndex(x => new { x.QualityProfileId, x.CustomFormatId }).IsUnique();
            b.HasOne<QualityProfileEntity>().WithMany().HasForeignKey(x => x.QualityProfileId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne<CustomFormatEntity>().WithMany().HasForeignKey(x => x.CustomFormatId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AirScheduleEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.ShowTmdbId, x.SeasonNumber, x.EpisodeNumber }).IsUnique();
            // The due scan — the scheduler reads the minimum of these to decide when to wake.
            b.HasIndex(x => x.NextSearchAt);
            b.HasIndex(x => x.MediaRequestId);
            b.HasIndex(x => new { x.Monitored, x.SearchState });
        });

        modelBuilder.Entity<MediaRequestEntity>(b =>
        {
            // SQLite permits multiple NULLs in a unique index, so ordinary user requests are unaffected.
            // A monitor anchor gets at most one reusable child per season.
            b.HasIndex(x => new { x.MonitoringAnchorId, x.MonitoringSeasonNumber }).IsUnique();
        });

        modelBuilder.Entity<MonitoringPreferencesEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.IsSingleton).IsUnique(); // enforce the single settings row
        });

        modelBuilder.Entity<RecommendedItemEntity>(b =>
        {
            b.HasKey(x => x.Id);
            // One row per resolved title; the feed refresh upserts rather than appending.
            b.HasIndex(x => new { x.TmdbId, x.MediaType }).IsUnique();
            b.HasIndex(x => x.Rank);
            b.HasIndex(x => x.SeenAt);
        });

        modelBuilder.Entity<IndexerEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.Name).IsUnique();          // display names are the admin's handle on a row
            b.HasIndex(x => new { x.Enabled, x.Priority }); // the downloader's fan-out query
            b.HasIndex(x => x.Implementation);
        });

        modelBuilder.Entity<IndexerMediaCapabilityEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => new { x.IndexerId, x.MediaType }).IsUnique();
            b.HasOne(x => x.Indexer).WithMany(x => x.MediaCapabilities)
                .HasForeignKey(x => x.IndexerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FirefoxCapturePairingEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.CodeHash).IsUnique();
            b.HasIndex(x => x.ExpiresAt);
            b.HasOne(x => x.Indexer).WithMany().HasForeignKey(x => x.IndexerId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FirefoxCaptureDeviceEntity>(b =>
        {
            b.HasKey(x => x.Id);
            b.HasIndex(x => x.TokenHash).IsUnique();
            b.HasIndex(x => x.PairingId).IsUnique();
            b.HasIndex(x => new { x.IndexerId, x.RevokedAt, x.ExpiresAt });
            b.HasOne(x => x.Pairing).WithMany().HasForeignKey(x => x.PairingId)
                .OnDelete(DeleteBehavior.Cascade);
            b.HasOne(x => x.Indexer).WithMany().HasForeignKey(x => x.IndexerId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
