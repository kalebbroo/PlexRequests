using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Capture;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

/// <summary>
/// Authenticates a Firefox capture device and translates rendered page observations into the catalog's
/// ordinary idempotent batch format. Browser cookies never cross this boundary.
/// </summary>
public sealed class FirefoxCaptureService(
    IDbContextFactory<AppDbContext> appFactory,
    IReleaseCatalogService catalog,
    IOptions<FirefoxCaptureOptions> captureOptions,
    IOptions<Infrastructure.Catalog.CatalogOptions> catalogOptions,
    FirefoxExtensionInfo extensionInfo,
    TimeProvider clock,
    ILogger<FirefoxCaptureService> logger) : IFirefoxCaptureService
{
    private const string SourceSuffix = " (Firefox)";
    private const string PairingAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public async Task<FirefoxCapturePairingDto> CreatePairingAsync(
        int indexerId,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var indexer = await GetSupportedIndexerAsync(db, indexerId, cancellationToken);

        var rawCode = CreatePairingCode();
        var entity = new FirefoxCapturePairingEntity
        {
            Id = Guid.NewGuid(),
            IndexerId = indexer.Id,
            CodeHash = Hash(NormalizePairingCode(rawCode)),
            CreatedAt = now,
            ExpiresAt = now.AddMinutes(Math.Clamp(captureOptions.Value.PairingLifetimeMinutes, 2, 60))
        };
        db.FirefoxCapturePairings.Add(entity);
        await db.SaveChangesAsync(cancellationToken);

        return new FirefoxCapturePairingDto { Code = rawCode, ExpiresAt = entity.ExpiresAt };
    }

    public async Task<FirefoxCapturePairResponseDto?> RedeemPairingAsync(
        FirefoxCapturePairRequestDto request,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var normalizedCode = NormalizePairingCode(request.PairingCode);
        if (normalizedCode.Length != 12) return null;

        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var codeHash = Hash(normalizedCode);
        var pairing = await db.FirefoxCapturePairings
            .Include(x => x.Indexer)
            .SingleOrDefaultAsync(x => x.CodeHash == codeHash, cancellationToken);
        if (pairing is null || pairing.UsedAt is not null || pairing.ExpiresAt <= now
            || !IsSupportedIndexer(pairing.Indexer))
            return null;

        var rawToken = "prfc_" + Base64Url(RandomNumberGenerator.GetBytes(32));
        var device = new FirefoxCaptureDeviceEntity
        {
            Id = Guid.NewGuid(),
            PairingId = pairing.Id,
            IndexerId = pairing.IndexerId,
            TokenHash = Hash(rawToken),
            Name = Clean(request.DeviceName, 128) ?? "Firefox",
            ExtensionVersion = Clean(request.ExtensionVersion, 32) ?? "unknown",
            CreatedAt = now,
            ExpiresAt = now.AddDays(Math.Clamp(captureOptions.Value.DeviceLifetimeDays, 1, 365))
        };
        pairing.UsedAt = now;
        db.FirefoxCaptureDevices.Add(device);

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is SqliteException { SqliteErrorCode: 19 })
        {
            // PairingId is unique on devices. A concurrent second redemption loses safely.
            return null;
        }

        logger.LogInformation("Firefox capture device {DeviceId} paired with indexer {IndexerId}",
            device.Id, device.IndexerId);
        var indexer = pairing.Indexer!;
        return new FirefoxCapturePairResponseDto
        {
            Token = rawToken,
            ExpiresAt = device.ExpiresAt,
            IndexerId = device.IndexerId,
            Implementation = indexer.Implementation,
            Source = SourceName(indexer.Name)
        };
    }

    public async Task<FirefoxCaptureConnectionDto?> GetConnectionAsync(
        string token,
        CancellationToken cancellationToken = default)
    {
        if (!captureOptions.Value.Enabled || string.IsNullOrWhiteSpace(token)) return null;
        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var device = await FindActiveDeviceAsync(db, token, now, cancellationToken);
        if (device is null) return null;
        device.LastSeenAt = now;
        RenewLeaseIfNeeded(device, now);
        await db.SaveChangesAsync(cancellationToken);
        return Connection(device, now);
    }

    public async Task<FirefoxCaptureConnectionDto?> RecordHeartbeatAsync(
        string token,
        FirefoxCaptureHeartbeatDto heartbeat,
        CancellationToken cancellationToken = default)
    {
        if (!captureOptions.Value.Enabled || string.IsNullOrWhiteSpace(token)) return null;
        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var device = await FindActiveDeviceAsync(db, token, now, cancellationToken);
        if (device is null) return null;
        ValidateHeartbeat(heartbeat, now);

        device.LastSeenAt = now;
        device.LastHeartbeatAt = now;
        device.CaptureEnabled = heartbeat.CaptureEnabled;
        device.QueuedUploads = heartbeat.QueuedUploads;
        device.FailedUploads = heartbeat.FailedUploads;
        device.PendingDetails = heartbeat.PendingDetails;
        device.AttentionDetails = heartbeat.AttentionDetails;
        device.HydrationPausedUntil = heartbeat.HydrationPausedUntil > now
            ? heartbeat.HydrationPausedUntil
            : null;
        device.ExtensionVersion = Clean(heartbeat.ExtensionVersion, 32) ?? device.ExtensionVersion;
        RenewLeaseIfNeeded(device, now);
        await db.SaveChangesAsync(cancellationToken);
        return Connection(device, now);
    }

    public async Task<CatalogHydrationPageDto?> GetPendingDetailsAsync(
        string token,
        long afterId,
        int limit,
        CancellationToken cancellationToken = default)
    {
        if (!captureOptions.Value.Enabled || !catalogOptions.Value.Enabled || string.IsNullOrWhiteSpace(token))
            return null;
        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var device = await FindActiveDeviceAsync(db, token, now, cancellationToken);
        if (device is null) return null;
        device.LastSeenAt = now;
        RenewLeaseIfNeeded(device, now);
        await db.SaveChangesAsync(cancellationToken);

        // EXT.to magnets depend on short-lived credentials from the listing page. Replaying its stored
        // detail URLs without those credentials would manufacture attention failures rather than recover work.
        if (!device.Indexer!.Implementation.Equals("1337x", StringComparison.OrdinalIgnoreCase))
            return new CatalogHydrationPageDto { NextCursor = Math.Max(0, afterId) };

        var page = await catalog.GetPendingHydrationAsync(
            device.IndexerId,
            SourceName(device.Indexer.Name),
            afterId,
            limit,
            cancellationToken);
        page.Items = page.Items
            .Where(item => TryAllowedUri(item.SourceUrl, device.Indexer, out _))
            .ToList();
        return page;
    }

    public async Task<FirefoxCaptureIngestResponseDto> IngestAsync(
        string token,
        FirefoxCaptureBatchDto batch,
        CancellationToken cancellationToken = default)
    {
        EnsureEnabled();
        var now = UtcNow;
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var device = await FindActiveDeviceAsync(db, token, now, cancellationToken)
            ?? throw new UnauthorizedAccessException("The Firefox capture token is invalid, expired, or revoked.");

        ValidateBatch(batch, device.Indexer!);
        var items = NormalizeItems(batch.Items);
        var result = await catalog.UpsertBatchAsync(new CatalogBatchDto
        {
            IndexerId = device.IndexerId,
            Source = SourceName(device.Indexer!.Name),
            BatchId = batch.BatchId.Trim(),
            AdvanceCheckpoint = false,
            ObservedAt = now,
            Items = items
        }, cancellationToken);

        device.LastSeenAt = now;
        device.LastCaptureAt = now;
        device.LastParserVersion = batch.ParserVersion;
        device.ExtensionVersion = Clean(batch.ExtensionVersion, 32) ?? device.ExtensionVersion;
        device.LastPageUrl = Clean(batch.PageUrl, 2048);
        RenewLeaseIfNeeded(device, now);
        if (!result.DuplicateBatch)
        {
            device.BatchesReceived++;
            device.ItemsReceived += items.Count;
        }
        await db.SaveChangesAsync(cancellationToken);

        return new FirefoxCaptureIngestResponseDto
        {
            DuplicateBatch = result.DuplicateBatch,
            AcceptedItems = items.Count,
            ReleasesInserted = result.ReleasesInserted,
            SightingsInserted = result.SightingsInserted,
            UnresolvedSightings = result.UnresolvedSightings
        };
    }

    public async Task<FirefoxCaptureAdminStatusDto> GetAdminStatusAsync(
        int indexerId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var indexer = await GetSupportedIndexerAsync(db, indexerId, cancellationToken);
        var now = UtcNow;
        var devices = await db.FirefoxCaptureDevices.AsNoTracking()
            .Where(x => x.IndexerId == indexerId)
            .OrderByDescending(x => x.CreatedAt)
            .ToListAsync(cancellationToken);
        return new FirefoxCaptureAdminStatusDto
        {
            Enabled = captureOptions.Value.Enabled,
            CatalogEnabled = catalogOptions.Value.Enabled,
            IndexerId = indexer.Id,
            IndexerName = indexer.Name,
            CurrentExtensionVersion = extensionInfo.CurrentVersion,
            Devices = devices.Select(x => new FirefoxCaptureDeviceDto
            {
                Id = x.Id,
                Name = x.Name,
                ExtensionVersion = x.ExtensionVersion,
                UpdateAvailable = extensionInfo.IsUpdateAvailable(x.ExtensionVersion),
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt,
                LastSeenAt = x.LastSeenAt,
                LastCaptureAt = x.LastCaptureAt,
                LastHeartbeatAt = x.LastHeartbeatAt,
                CaptureEnabled = x.CaptureEnabled,
                QueuedUploads = x.QueuedUploads,
                FailedUploads = x.FailedUploads,
                PendingDetails = x.PendingDetails,
                AttentionDetails = x.AttentionDetails,
                HydrationPausedUntil = x.HydrationPausedUntil,
                BatchesReceived = x.BatchesReceived,
                ItemsReceived = x.ItemsReceived,
                LastParserVersion = x.LastParserVersion,
                LastPageUrl = x.LastPageUrl,
                RevokedAt = x.RevokedAt,
                IsActive = x.RevokedAt is null && x.ExpiresAt > now
            }).ToList()
        };
    }

    public async Task<bool> RevokeDeviceAsync(Guid deviceId, CancellationToken cancellationToken = default)
    {
        await using var db = await appFactory.CreateDbContextAsync(cancellationToken);
        var device = await db.FirefoxCaptureDevices.SingleOrDefaultAsync(x => x.Id == deviceId, cancellationToken);
        if (device is null) return false;
        device.RevokedAt ??= UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        logger.LogInformation("Firefox capture device {DeviceId} revoked", deviceId);
        return true;
    }

    private void EnsureEnabled()
    {
        if (!captureOptions.Value.Enabled)
            throw new InvalidOperationException("Firefox browser capture is disabled.");
        if (!catalogOptions.Value.Enabled)
            throw new InvalidOperationException("The release catalog must be enabled before browser capture can be used.");
    }

    private async Task<IndexerEntity> GetSupportedIndexerAsync(
        AppDbContext db,
        int indexerId,
        CancellationToken cancellationToken)
    {
        var indexer = await db.Indexers.SingleOrDefaultAsync(x => x.Id == indexerId, cancellationToken);
        if (!IsSupportedIndexer(indexer))
            throw new ArgumentException("Firefox capture supports configured 1337x and ext.to indexers.");
        return indexer!;
    }

    private async Task<FirefoxCaptureDeviceEntity?> FindActiveDeviceAsync(
        AppDbContext db,
        string token,
        DateTime now,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) return null;
        var tokenHash = Hash(token.Trim());
        return await db.FirefoxCaptureDevices.Include(x => x.Indexer)
            .SingleOrDefaultAsync(x => x.TokenHash == tokenHash
                && x.RevokedAt == null
                && x.ExpiresAt > now,
                cancellationToken);
    }

    private void RenewLeaseIfNeeded(FirefoxCaptureDeviceEntity device, DateTime now)
    {
        var lifetimeDays = Math.Clamp(captureOptions.Value.DeviceLifetimeDays, 1, 365);
        var renewalWindowDays = Math.Clamp(captureOptions.Value.DeviceRenewalWindowDays, 1, lifetimeDays);
        if (device.ExpiresAt <= now.AddDays(renewalWindowDays))
            device.ExpiresAt = now.AddDays(lifetimeDays);
    }

    private FirefoxCaptureConnectionDto Connection(FirefoxCaptureDeviceEntity device, DateTime now)
    {
        var indexer = device.Indexer!;
        return new FirefoxCaptureConnectionDto
        {
            Connected = true,
            ServerTime = now,
            ExpiresAt = device.ExpiresAt,
            Implementation = indexer.Implementation,
            Source = SourceName(indexer.Name),
            CurrentExtensionVersion = extensionInfo.CurrentVersion
        };
    }

    private void ValidateBatch(FirefoxCaptureBatchDto batch, IndexerEntity indexer)
    {
        if (string.IsNullOrWhiteSpace(batch.BatchId) || batch.BatchId.Length > 128)
            throw new ArgumentException("BatchId is required and must be 128 characters or fewer.");
        if (batch.ParserVersion <= 0 || batch.ParserVersion > 100_000)
            throw new ArgumentException("ParserVersion is invalid.");
        if (batch.ExtensionVersion?.Length > 32)
            throw new ArgumentException("ExtensionVersion must be 32 characters or fewer.");
        if (batch.PageType is not ("listing" or "detail"))
            throw new ArgumentException("PageType must be listing or detail.");
        if (batch.Items is null || batch.Items.Count == 0
            || batch.Items.Count > Math.Clamp(captureOptions.Value.MaxBatchSize, 1, 1000))
            throw new ArgumentException($"A capture batch must contain 1-{captureOptions.Value.MaxBatchSize} items.");
        if (!TryAllowedUri(batch.PageUrl, indexer, out _))
            throw new ArgumentException($"The captured page is not on an allowed {indexer.Implementation} host.");

        foreach (var item in batch.Items)
        {
            if (string.IsNullOrWhiteSpace(item.ExternalId) || item.ExternalId.Length > 512
                || string.IsNullOrWhiteSpace(item.ReleaseName) || item.ReleaseName.Length > 512)
                throw new ArgumentException("Every captured item needs an external ID and release name of 512 characters or fewer.");
            if (item.SourceUrl is not null && !TryAllowedUri(item.SourceUrl, indexer, out _))
                throw new ArgumentException("A captured item contains a source URL outside the allowed host list.");
            if (item.MagnetUri is not null
                && (!Uri.TryCreate(item.MagnetUri, UriKind.Absolute, out var magnet)
                    || !magnet.Scheme.Equals("magnet", StringComparison.OrdinalIgnoreCase)
                    || item.MagnetUri.Length > 4096))
                throw new ArgumentException("A captured item contains an invalid magnet URI.");
        }
    }

    private static void ValidateHeartbeat(FirefoxCaptureHeartbeatDto heartbeat, DateTime now)
    {
        if (heartbeat.ExtensionVersion?.Length > 32)
            throw new ArgumentException("ExtensionVersion must be 32 characters or fewer.");
        if (heartbeat.QueuedUploads is < 0 or > 10_000
            || heartbeat.FailedUploads is < 0 or > 10_000
            || heartbeat.PendingDetails is < 0 or > 10_000
            || heartbeat.AttentionDetails is < 0 or > 10_000)
            throw new ArgumentException("Firefox queue counts must be between 0 and 10,000.");
        if (heartbeat.HydrationPausedUntil is { } pausedUntil
            && pausedUntil > now.AddDays(1))
            throw new ArgumentException("HydrationPausedUntil is outside the allowed range.");
    }

    private List<CatalogItemDto> NormalizeItems(IEnumerable<CatalogItemDto> items) => items
        .GroupBy(x => x.ExternalId.Trim(), StringComparer.Ordinal)
        .Select(group => group.OrderByDescending(x => !string.IsNullOrWhiteSpace(x.InfoHash)
                                                   || !string.IsNullOrWhiteSpace(x.MagnetUri)).First())
        .Select(x => new CatalogItemDto
        {
            ExternalId = x.ExternalId.Trim(),
            ReleaseName = x.ReleaseName.Trim(),
            SourceUrl = Clean(x.SourceUrl, 2048),
            InfoHash = Clean(x.InfoHash, 64),
            MagnetUri = Clean(x.MagnetUri, 4096),
            ImdbId = Clean(x.ImdbId, 32),
            Category = Clean(x.Category, 128),
            Uploader = Clean(x.Uploader, 128),
            Seeders = x.Seeders is >= 0 ? x.Seeders : null,
            Leechers = x.Leechers is >= 0 ? x.Leechers : null,
            SizeBytes = x.SizeBytes is > 0 and <= 109_951_162_777_600 ? x.SizeBytes : null,
            PublishedAt = x.PublishedAt,
            MediaType = x.MediaType,
            NeedsHydration = string.IsNullOrWhiteSpace(x.InfoHash) && string.IsNullOrWhiteSpace(x.MagnetUri)
        }).ToList();

    private bool TryAllowedUri(string? value, IndexerEntity indexer, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 2048
            || !Uri.TryCreate(value, UriKind.Absolute, out var candidate)
            || candidate.Scheme is not ("http" or "https")
            || !string.IsNullOrEmpty(candidate.UserInfo))
            return false;

        var configuredHosts = captureOptions.Value.AllowedHostsByIndexer
            .FirstOrDefault(entry => entry.Key.Equals(indexer.Implementation, StringComparison.OrdinalIgnoreCase)).Value;
        var allowed = (configuredHosts ?? [])
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Select(x => x.Trim().Trim('.'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (Uri.TryCreate(indexer.BaseUrlOverride, UriKind.Absolute, out var configured))
            allowed.Add(configured.Host);

        if (!allowed.Any(host => candidate.Host.Equals(host, StringComparison.OrdinalIgnoreCase)
                              || candidate.Host.EndsWith("." + host, StringComparison.OrdinalIgnoreCase)))
            return false;
        uri = candidate;
        return true;
    }

    private static bool IsSupportedIndexer(IndexerEntity? indexer) =>
        indexer is not null && (indexer.Implementation.Equals("1337x", StringComparison.OrdinalIgnoreCase)
            || indexer.Implementation.Equals("ext.to", StringComparison.OrdinalIgnoreCase));

    private DateTime UtcNow => clock.GetUtcNow().UtcDateTime;
    private static string SourceName(string indexerName) => Clean(indexerName, 110) + SourceSuffix;
    private static string NormalizePairingCode(string? value) => new((value ?? string.Empty)
        .Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    private static string Base64Url(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string CreatePairingCode()
    {
        Span<byte> random = stackalloc byte[12];
        RandomNumberGenerator.Fill(random);
        Span<char> chars = stackalloc char[14];
        var output = 0;
        for (var i = 0; i < random.Length; i++)
        {
            if (i is 4 or 8) chars[output++] = '-';
            chars[output++] = PairingAlphabet[random[i] % PairingAlphabet.Length];
        }
        return new string(chars);
    }

    private static string? Clean(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }
}
