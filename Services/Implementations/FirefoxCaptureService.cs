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
        return new FirefoxCapturePairResponseDto
        {
            Token = rawToken,
            ExpiresAt = device.ExpiresAt,
            IndexerId = device.IndexerId,
            Source = SourceName(pairing.Indexer!.Name)
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
        await db.SaveChangesAsync(cancellationToken);
        return new FirefoxCaptureConnectionDto
        {
            Connected = true,
            ServerTime = now,
            ExpiresAt = device.ExpiresAt,
            Source = SourceName(device.Indexer!.Name)
        };
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
            Devices = devices.Select(x => new FirefoxCaptureDeviceDto
            {
                Id = x.Id,
                Name = x.Name,
                ExtensionVersion = x.ExtensionVersion,
                CreatedAt = x.CreatedAt,
                ExpiresAt = x.ExpiresAt,
                LastSeenAt = x.LastSeenAt,
                LastCaptureAt = x.LastCaptureAt,
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
            throw new ArgumentException("Firefox capture currently supports only a configured 1337x indexer.");
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
            throw new ArgumentException("The captured page is not on an allowed 1337x host.");

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

        var allowed = (captureOptions.Value.AllowedHosts ?? [])
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
        indexer is not null && indexer.Implementation.Equals("1337x", StringComparison.OrdinalIgnoreCase);

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
