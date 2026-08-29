using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Catalog;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;
using PlexRequestsHosted.Shared.Releases;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Single-writer catalog boundary. The downloader may retry any batch after a timeout; the receipt ledger,
/// source identities, and infohash uniqueness turn those retries into no-ops or updates instead of duplicates.
/// </summary>
public sealed class ReleaseCatalogService(
    IDbContextFactory<CatalogDbContext> contextFactory,
    IReleaseParser parser,
    IOptions<CatalogOptions> options,
    ILogger<ReleaseCatalogService> logger) : IReleaseCatalogService
{
    public const int ParserVersion = 1;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    public async Task<CatalogUpsertResultDto> UpsertBatchAsync(
        CatalogBatchDto batch,
        CancellationToken cancellationToken)
    {
        Validate(batch);
        var observedAt = batch.ObservedAt == default ? DateTime.UtcNow : batch.ObservedAt.ToUniversalTime();

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            if (await db.BatchReceipts.AnyAsync(
                    x => x.IndexerId == batch.IndexerId && x.BatchId == batch.BatchId,
                    cancellationToken))
                return new CatalogUpsertResultDto { DuplicateBatch = true };

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var externalIds = batch.Items.Select(x => x.ExternalId.Trim()).Distinct().ToList();
            var sightings = await db.Sightings
                .Where(x => x.IndexerId == batch.IndexerId && externalIds.Contains(x.ExternalId))
                .ToDictionaryAsync(x => x.ExternalId, StringComparer.Ordinal, cancellationToken);

            var hashes = batch.Items.Select(x => NormalizeInfoHash(x.InfoHash, x.MagnetUri))
                .Where(x => x is not null).Cast<string>().Distinct().ToList();
            var releases = await db.Releases.Where(x => hashes.Contains(x.InfoHash))
                .ToDictionaryAsync(x => x.InfoHash, StringComparer.Ordinal, cancellationToken);

            var result = new CatalogUpsertResultDto();
            foreach (var item in batch.Items)
            {
                var externalId = item.ExternalId.Trim();
                var releaseName = item.ReleaseName.Trim();
                var infoHash = NormalizeInfoHash(item.InfoHash, item.MagnetUri);
                CatalogReleaseEntity? release = null;

                if (infoHash is not null)
                {
                    if (!releases.TryGetValue(infoHash, out release))
                    {
                        release = CreateRelease(infoHash, releaseName, item, observedAt);
                        db.Releases.Add(release);
                        releases.Add(infoHash, release);
                        result.ReleasesInserted++;
                    }
                    else
                    {
                        MergeRelease(release, item, observedAt);
                        result.ReleasesUpdated++;
                    }
                }

                if (!sightings.TryGetValue(externalId, out var sighting))
                {
                    sighting = new CatalogSightingEntity
                    {
                        IndexerId = batch.IndexerId,
                        Source = batch.Source.Trim(),
                        ExternalId = externalId,
                        FirstSeenAt = observedAt
                    };
                    db.Sightings.Add(sighting);
                    sightings.Add(externalId, sighting);
                    result.SightingsInserted++;
                }
                else
                {
                    result.SightingsUpdated++;
                }

                // A transient summary row may omit an infohash that an earlier hydration already found.
                // Never detach that durable identity; only replace an unresolved sighting with new evidence.
                if (release is not null) sighting.Release = release;
                sighting.ReleaseName = releaseName;
                sighting.SourceUrl = Clean(item.SourceUrl, 2048);
                sighting.Category = Clean(item.Category, 128);
                sighting.Uploader = Clean(item.Uploader, 128);
                sighting.Seeders = item.Seeders;
                sighting.Leechers = item.Leechers;
                sighting.SizeBytes = item.SizeBytes is > 0 ? item.SizeBytes : null;
                sighting.PublishedAt = item.PublishedAt?.ToUniversalTime();
                sighting.LastSeenAt = observedAt;
                var isResolved = release is not null || sighting.ReleaseId is not null;
                if (isResolved)
                {
                    sighting.HydrationState = CatalogHydrationState.Hydrated;
                    sighting.LastHydratedAt = observedAt;
                    sighting.HydrationError = null;
                }
                else if (sighting.HydrationState != CatalogHydrationState.Failed)
                {
                    // A repeated listing observation must not revive an immutable source id whose detail
                    // page authoritatively reported deletion. A later detail carrying a real hash still
                    // enters the resolved branch above and self-heals the row.
                    sighting.HydrationState = item.NeedsHydration
                        ? CatalogHydrationState.Pending
                        : CatalogHydrationState.NotRequired;
                }
                if (!isResolved) result.UnresolvedSightings++;
            }

            if (batch.AdvanceCheckpoint)
            {
                var checkpoint = await db.Checkpoints.SingleOrDefaultAsync(
                    x => x.IndexerId == batch.IndexerId, cancellationToken);
                if (checkpoint is null)
                {
                    checkpoint = new CatalogCheckpointEntity { IndexerId = batch.IndexerId };
                    db.Checkpoints.Add(checkpoint);
                }
                checkpoint.Source = batch.Source.Trim();
                checkpoint.Cursor = Clean(batch.Cursor, 2048);
                checkpoint.LastBatchId = batch.BatchId.Trim();
                checkpoint.LastAttemptAt = observedAt;
                checkpoint.LastSuccessAt = observedAt;
                checkpoint.NextAttemptAt = null;
                checkpoint.ConsecutiveFailures = 0;
                checkpoint.CircuitState = CatalogCircuitState.Closed;
                checkpoint.LastError = null;
                checkpoint.TotalItemsSeen += batch.Items.Count;
                checkpoint.TotalItemsResolved += batch.Items.Count - result.UnresolvedSightings;
            }

            db.BatchReceipts.Add(new CatalogBatchReceiptEntity
            {
                IndexerId = batch.IndexerId,
                BatchId = batch.BatchId.Trim(),
                ItemCount = batch.Items.Count,
                CommittedAt = observedAt
            });

            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Catalog batch {BatchId} from {Source} failed", batch.BatchId, batch.Source);
            throw;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<CatalogHydrationPageDto> GetPendingHydrationAsync(
        int indexerId,
        string source,
        long afterId,
        int limit,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || indexerId <= 0 || string.IsNullOrWhiteSpace(source))
            return new CatalogHydrationPageDto { NextCursor = Math.Max(0, afterId) };

        var cursor = Math.Max(0, afterId);
        var pageSize = Math.Clamp(limit, 1, 250);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Sightings.AsNoTracking()
            .Where(x => x.Id > cursor
                && x.IndexerId == indexerId
                && x.Source == source
                && x.ReleaseId == null
                && x.HydrationState == CatalogHydrationState.Pending
                && x.SourceUrl != null)
            .OrderBy(x => x.Id)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new CatalogHydrationPageDto
        {
            NextCursor = rows.LastOrDefault()?.Id ?? cursor,
            Items = rows.Select(x => new CatalogItemDto
            {
                ExternalId = x.ExternalId,
                ReleaseName = x.ReleaseName,
                SourceUrl = x.SourceUrl,
                Category = x.Category,
                Uploader = x.Uploader,
                Seeders = x.Seeders,
                Leechers = x.Leechers,
                SizeBytes = x.SizeBytes,
                PublishedAt = x.PublishedAt,
                NeedsHydration = true
            }).ToList()
        };
    }

    public async Task<bool> MarkHydrationFailedAsync(
        CatalogHydrationFailureDto failure,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return false;
        if (failure.IndexerId <= 0)
            throw new ArgumentException("IndexerId must be positive.");
        if (string.IsNullOrWhiteSpace(failure.Source) || failure.Source.Length > 128
            || string.IsNullOrWhiteSpace(failure.ExternalId) || failure.ExternalId.Length > 512
            || string.IsNullOrWhiteSpace(failure.SourceUrl) || failure.SourceUrl.Length > 2048
            || string.IsNullOrWhiteSpace(failure.Error) || failure.Error.Length > 512)
            throw new ArgumentException("A hydration failure has invalid or oversized fields.");

        var occurredAt = failure.OccurredAt == default
            ? DateTime.UtcNow
            : failure.OccurredAt.ToUniversalTime();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var sighting = await db.Sightings.SingleOrDefaultAsync(x =>
                x.IndexerId == failure.IndexerId
                && x.Source == failure.Source.Trim()
                && x.ExternalId == failure.ExternalId.Trim(), cancellationToken);
            if (sighting is null || sighting.ReleaseId is not null
                || sighting.HydrationState == CatalogHydrationState.Hydrated)
                return false;

            sighting.HydrationState = CatalogHydrationState.Failed;
            sighting.HydrationError = failure.Error.Trim();
            sighting.LastHydratedAt = occurredAt;
            await db.SaveChangesAsync(cancellationToken);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<CatalogStatsDto> GetStatsAsync(CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled) return new CatalogStatsDto();
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var sightingSources = await db.Sightings
            .GroupBy(x => new { x.IndexerId, x.Source })
            .Select(group => new
            {
                group.Key.IndexerId,
                group.Key.Source,
                Sightings = group.LongCount(),
                Resolved = group.LongCount(x => x.ReleaseId != null),
                LastSeenAt = group.Max(x => (DateTime?)x.LastSeenAt)
            })
            .OrderBy(x => x.Source)
            .ToListAsync(cancellationToken);
        var checkpoints = await db.Checkpoints.AsNoTracking().OrderBy(x => x.Source)
            .ToListAsync(cancellationToken);
        var sources = sightingSources.Select(sighting =>
        {
            var checkpoint = checkpoints.FirstOrDefault(candidate =>
                candidate.IndexerId == sighting.IndexerId
                && candidate.Source.Equals(sighting.Source, StringComparison.OrdinalIgnoreCase));
            return new CatalogSourceStatsDto
            {
                IndexerId = sighting.IndexerId,
                Source = sighting.Source,
                Sightings = sighting.Sightings,
                TotalItemsSeen = sighting.Sightings,
                TotalItemsResolved = sighting.Resolved,
                LastSuccessAt = Latest(checkpoint?.LastSuccessAt, sighting.LastSeenAt),
                NextAttemptAt = checkpoint?.NextAttemptAt,
                CircuitState = checkpoint?.CircuitState.ToString() ?? CatalogCircuitState.Closed.ToString(),
                LastError = checkpoint?.LastError
            };
        }).ToList();
        sources.AddRange(checkpoints
            .Where(checkpoint => !sightingSources.Any(sighting =>
                sighting.IndexerId == checkpoint.IndexerId
                && sighting.Source.Equals(checkpoint.Source, StringComparison.OrdinalIgnoreCase)))
            .Select(checkpoint => new CatalogSourceStatsDto
            {
                IndexerId = checkpoint.IndexerId,
                Source = checkpoint.Source,
                TotalItemsSeen = checkpoint.TotalItemsSeen,
                TotalItemsResolved = checkpoint.TotalItemsResolved,
                LastSuccessAt = checkpoint.LastSuccessAt,
                NextAttemptAt = checkpoint.NextAttemptAt,
                CircuitState = checkpoint.CircuitState.ToString(),
                LastError = checkpoint.LastError
            }));
        var dataSource = db.Database.GetDbConnection().DataSource;
        return new CatalogStatsDto
        {
            Enabled = true,
            UseForSearch = options.Value.UseForSearch,
            UseForMonitoring = options.Value.UseForMonitoring,
            DatabaseSizeBytes = File.Exists(dataSource) ? new FileInfo(dataSource).Length : 0,
            Releases = await db.Releases.LongCountAsync(cancellationToken),
            Sightings = await db.Sightings.LongCountAsync(cancellationToken),
            UnresolvedSightings = await db.Sightings.LongCountAsync(
                x => x.ReleaseId == null, cancellationToken),
            OldestReleaseAt = await db.Releases.Select(x => (DateTime?)x.FirstSeenAt).MinAsync(cancellationToken),
            NewestReleaseAt = await db.Releases.Select(x => (DateTime?)x.LastSeenAt).MaxAsync(cancellationToken),
            Sources = sources.OrderBy(x => x.Source).ToList()
        };
    }

    private static DateTime? Latest(DateTime? first, DateTime? second) => (first, second) switch
    {
        (null, null) => null,
        (null, _) => second,
        (_, null) => first,
        _ => first.Value > second.Value ? first : second
    };

    public async Task<CatalogCheckpointDto> GetCheckpointAsync(
        int indexerId,
        CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.Checkpoints.AsNoTracking().SingleOrDefaultAsync(
            x => x.IndexerId == indexerId, cancellationToken);
        return ToDto(row, indexerId);
    }

    public async Task<CatalogCheckpointDto> ReportFailureAsync(
        CatalogFailureDto failure,
        CancellationToken cancellationToken)
    {
        if (failure.IndexerId <= 0) throw new ArgumentException("IndexerId must be positive.");
        if (string.IsNullOrWhiteSpace(failure.Source)) throw new ArgumentException("Source is required.");

        var now = failure.OccurredAt == default ? DateTime.UtcNow : failure.OccurredAt.ToUniversalTime();
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.Checkpoints.SingleOrDefaultAsync(
                x => x.IndexerId == failure.IndexerId, cancellationToken);
            if (row is null)
            {
                row = new CatalogCheckpointEntity { IndexerId = failure.IndexerId };
                db.Checkpoints.Add(row);
            }

            row.Source = Clean(failure.Source, 128)!;
            row.LastAttemptAt = now;
            row.ConsecutiveFailures++;
            row.CircuitState = CatalogCircuitState.Open;
            row.LastError = Clean(failure.Error, 512) ?? "Ingestion failed.";
            row.NextAttemptAt = now + FailureDelay(failure, row.ConsecutiveFailures);
            await db.SaveChangesAsync(cancellationToken);
            return ToDto(row, failure.IndexerId);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task<IReadOnlyList<ReleaseCandidate>> SearchAsync(
        CatalogQueryDto query,
        CancellationToken cancellationToken)
    {
        if (!options.Value.Enabled || string.IsNullOrWhiteSpace(query.Title))
            return Array.Empty<ReleaseCandidate>();

        var parsedTitle = parser.Parse(query.Title).Title;
        var normalized = parsedTitle.ToUpperInvariant();
        var imdb = Clean(query.ImdbId, 32);
        var limit = Math.Clamp(query.Limit, 1, 1000);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var releases = await db.Releases.AsNoTracking()
            .Include(x => x.Sightings)
            .Where(x => (x.MediaType == null || x.MediaType == query.MediaType)
                && (query.Year == null || x.Year == null || x.Year == query.Year)
                && ((imdb != null && x.ImdbId == imdb)
                    || x.NormalizedTitle == normalized
                    || x.NormalizedTitle.StartsWith(normalized + " ")))
            .OrderByDescending(x => x.LastSeenAt)
            .Take(limit)
            .ToListAsync(cancellationToken);

        return releases.Select(release =>
        {
            var sighting = release.Sightings
                .OrderByDescending(x => x.Seeders ?? -1)
                .ThenByDescending(x => x.LastSeenAt)
                .FirstOrDefault();
            return new ReleaseCandidate
            {
                ReleaseName = release.ReleaseName,
                Acquisition = AcquisitionResource.Torrent(
                    release.MagnetUri ?? BuildMinimalMagnet(release.InfoHash, release.ReleaseName), release.InfoHash),
                ImdbId = release.ImdbId,
                Seeders = sighting?.Seeders ?? 0,
                Leechers = sighting?.Leechers ?? 0,
                SizeBytes = release.SizeBytes ?? sighting?.SizeBytes ?? 0,
                IndexerId = sighting?.IndexerId ?? 0,
                Source = sighting?.Source ?? "Catalog",
                QualityLabel = release.Resolution > 0 ? $"{release.Resolution}p" : null,
                Season = release.Season,
                Episode = release.Episode,
                PublishDate = release.PublishedAt,
                SeedersKnown = sighting?.Seeders is not null,
                SizeKnown = release.SizeBytes is not null || sighting?.SizeBytes is not null
            };
        }).ToList();
    }

    public async Task<CatalogBrowsePageDto> BrowseAsync(
        CatalogBrowseQueryDto query,
        CancellationToken cancellationToken)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 10, 100);
        if (!options.Value.Enabled)
            return new CatalogBrowsePageDto { Page = page, PageSize = pageSize };

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var releases = db.Releases.AsNoTracking()
            .Where(release => release.Sightings.Any());
        if (query.IndexerId is int indexerId and > 0)
            releases = releases.Where(release => release.Sightings.Any(sighting => sighting.IndexerId == indexerId));

        releases = query.MediaType switch
        {
            MediaType.Movie => releases.Where(release => release.MediaType == MediaType.Movie
                || (release.MediaType == null && release.Season == null)),
            MediaType.TvShow => releases.Where(release => release.MediaType == MediaType.TvShow
                || (release.MediaType == null && release.Season != null)),
            MediaType.Anime => releases.Where(release => release.MediaType == MediaType.Anime),
            MediaType.Music => releases.Where(release => release.MediaType == MediaType.Music),
            _ => releases
        };

        var search = Clean(query.Search, 256);
        if (search is not null)
        {
            var parsed = parser.Parse(search).Title;
            var tokens = (string.IsNullOrWhiteSpace(parsed) ? search : parsed)
                .ToUpperInvariant()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.Ordinal)
                .Take(8)
                .ToList();
            foreach (var token in tokens)
            {
                var term = token;
                releases = releases.Where(release => release.NormalizedTitle.Contains(term));
            }
        }

        var total = await releases.LongCountAsync(cancellationToken);
        var maxPage = Math.Max(1, (int)Math.Min(int.MaxValue / pageSize,
            (total + pageSize - 1) / pageSize));
        page = Math.Min(page, maxPage);
        var rows = await releases
            .Include(release => release.Sightings)
            .OrderByDescending(release => release.LastSeenAt)
            .ThenBy(release => release.ReleaseName)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsSplitQuery()
            .ToListAsync(cancellationToken);

        return new CatalogBrowsePageDto
        {
            Total = total,
            Page = page,
            PageSize = pageSize,
            Items = rows.Select(release =>
            {
                var sightings = query.IndexerId is int selected and > 0
                    ? release.Sightings.Where(sighting => sighting.IndexerId == selected).ToList()
                    : release.Sightings.ToList();
                var best = sightings
                    .OrderByDescending(sighting => sighting.Seeders ?? -1)
                    .ThenByDescending(sighting => sighting.LastSeenAt)
                    .First();
                return new CatalogBrowseItemDto
                {
                    ReleaseName = release.ReleaseName,
                    MediaType = release.MediaType ?? (release.Season is not null ? MediaType.TvShow : null),
                    Year = release.Year,
                    Season = release.Season,
                    Episode = release.Episode,
                    IsSeasonPack = release.IsSeasonPack,
                    Resolution = release.Resolution,
                    ReleaseSource = release.ReleaseSource,
                    Codec = release.Codec,
                    SizeBytes = release.SizeBytes ?? best.SizeBytes,
                    PublishedAt = release.PublishedAt ?? best.PublishedAt,
                    LastSeenAt = release.LastSeenAt,
                    IndexerId = best.IndexerId,
                    Source = best.Source,
                    SourceUrl = SafeSourceUrl(best.SourceUrl),
                    Category = best.Category,
                    Uploader = best.Uploader,
                    Seeders = best.Seeders,
                    Leechers = best.Leechers,
                    SightingCount = release.Sightings.Count
                };
            }).ToList()
        };
    }

    private static string BuildMinimalMagnet(string infoHash, string releaseName) =>
        $"magnet:?xt=urn:btih:{infoHash}&dn={Uri.EscapeDataString(releaseName)}";

    private static string? SafeSourceUrl(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? uri.ToString()
            : null;

    internal static TimeSpan FailureDelay(CatalogFailureDto failure, int consecutiveFailures)
    {
        if (failure.RetryAfterSeconds is > 0)
            return TimeSpan.FromSeconds(Math.Clamp(failure.RetryAfterSeconds.Value, 30, 86_400));
        return failure.BlockReason switch
        {
            Shared.Enums.IndexerBlockReason.CloudflareChallenge => TimeSpan.FromHours(6),
            Shared.Enums.IndexerBlockReason.IpBanned => TimeSpan.FromHours(12),
            Shared.Enums.IndexerBlockReason.Forbidden => TimeSpan.FromHours(6),
            Shared.Enums.IndexerBlockReason.RateLimited => TimeSpan.FromMinutes(30),
            _ => TimeSpan.FromSeconds(Math.Min(3600, 30 * Math.Pow(2, Math.Clamp(consecutiveFailures - 1, 0, 7))))
        };
    }

    private static CatalogCheckpointDto ToDto(CatalogCheckpointEntity? row, int indexerId) => new()
    {
        IndexerId = indexerId,
        Cursor = row?.Cursor,
        LastSuccessAt = row?.LastSuccessAt,
        NextAttemptAt = row?.NextAttemptAt,
        ConsecutiveFailures = row?.ConsecutiveFailures ?? 0,
        CircuitState = row?.CircuitState.ToString() ?? CatalogCircuitState.Closed.ToString(),
        LastError = row?.LastError
    };

    private void Validate(CatalogBatchDto batch)
    {
        if (batch.IndexerId <= 0) throw new ArgumentException("IndexerId must be positive.");
        if (string.IsNullOrWhiteSpace(batch.Source) || batch.Source.Length > 128)
            throw new ArgumentException("Source is required and must be 128 characters or fewer.");
        if (string.IsNullOrWhiteSpace(batch.BatchId) || batch.BatchId.Length > 128)
            throw new ArgumentException("BatchId is required and must be 128 characters or fewer.");
        if (batch.Items.Count > Math.Clamp(options.Value.MaxBatchSize, 1, 1000))
            throw new ArgumentException($"A catalog batch cannot exceed {options.Value.MaxBatchSize} items.");
        if (batch.Items.Any(x => string.IsNullOrWhiteSpace(x.ExternalId) || x.ExternalId.Length > 512))
            throw new ArgumentException("Every catalog item needs an ExternalId of 512 characters or fewer.");
        if (batch.Items.Any(x => string.IsNullOrWhiteSpace(x.ReleaseName) || x.ReleaseName.Length > 512))
            throw new ArgumentException("Every catalog item needs a release name of 512 characters or fewer.");
        if (batch.Items.Select(x => x.ExternalId.Trim()).Distinct(StringComparer.Ordinal).Count() != batch.Items.Count)
            throw new ArgumentException("A catalog batch cannot contain duplicate ExternalIds.");
    }

    private CatalogReleaseEntity CreateRelease(
        string infoHash,
        string releaseName,
        CatalogItemDto item,
        DateTime observedAt)
    {
        var parsed = parser.Parse(releaseName);
        return new CatalogReleaseEntity
        {
            InfoHash = infoHash,
            ReleaseName = releaseName,
            NormalizedTitle = parsed.Title.ToUpperInvariant(),
            MagnetUri = Clean(item.MagnetUri, 4096),
            ImdbId = Clean(item.ImdbId, 32),
            SizeBytes = item.SizeBytes is > 0 ? item.SizeBytes : null,
            PublishedAt = item.PublishedAt?.ToUniversalTime(),
            FirstSeenAt = observedAt,
            LastSeenAt = observedAt,
            ParserVersion = ParserVersion,
            MediaType = item.MediaType,
            Year = parsed.Year,
            Season = parsed.Season,
            SeasonEnd = parsed.SeasonEnd,
            Episode = parsed.Episode,
            EpisodeStart = parsed.EpisodeStart,
            EpisodeEnd = parsed.EpisodeEnd,
            IsSeasonPack = parsed.IsSeasonPack,
            IsCompleteSeries = parsed.LooksLikeCompleteSeries,
            Resolution = parsed.Resolution,
            ReleaseSource = parsed.Source,
            Codec = parsed.Codec
        };
    }

    private static void MergeRelease(CatalogReleaseEntity release, CatalogItemDto item, DateTime observedAt)
    {
        release.LastSeenAt = observedAt > release.LastSeenAt ? observedAt : release.LastSeenAt;
        release.MagnetUri ??= Clean(item.MagnetUri, 4096);
        release.ImdbId ??= Clean(item.ImdbId, 32);
        if (release.SizeBytes is null && item.SizeBytes is > 0) release.SizeBytes = item.SizeBytes;
        if (release.PublishedAt is null && item.PublishedAt is not null)
            release.PublishedAt = item.PublishedAt.Value.ToUniversalTime();
        release.MediaType ??= item.MediaType;
    }

    internal static string? NormalizeInfoHash(string? explicitHash, string? magnetUri)
        => MagnetUtil.Normalize(explicitHash) ?? MagnetUtil.InfoHashFromMagnet(magnetUri);

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

}
