using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PlexRequestsHosted.Infrastructure.Data;
using PlexRequestsHosted.Infrastructure.Entities;
using PlexRequestsHosted.Services.Abstractions;
using PlexRequestsHosted.Shared.DTOs;

namespace PlexRequestsHosted.Services.Implementations;

public sealed class DirectVideoSettingsService(AppDbContext db, IOptions<YouTubeDataOptions> options)
    : IDirectVideoSettingsService
{
    private static readonly char[] Separators = ['\n', '\r', ',', ' ', '\t'];

    public async Task<DirectVideoSettingsDto> GetAsync(CancellationToken cancellationToken = default)
    {
        var row = await GetOrCreateAsync(cancellationToken);
        return new DirectVideoSettingsDto
        {
            Enabled = row.Enabled,
            ChannelIds = Parse(row.ChannelAllowlist),
            ApiKeyConfigured = !string.IsNullOrWhiteSpace(options.Value.ApiKey),
            UpdatedAt = row.UpdatedAt
        };
    }

    public async Task UpdateAsync(DirectVideoSettingsDto settings, CancellationToken cancellationToken = default)
    {
        var channels = settings.ChannelIds.Select(x => x.Trim()).Where(x => x.Length > 0)
            .Distinct(StringComparer.Ordinal).ToList();
        var invalid = channels.Where(x => !YouTubeDataVideoCatalog.IsChannelId(x)).ToList();
        if (invalid.Count > 0)
            throw new InvalidOperationException(
                $"Not a YouTube channel id: {string.Join(", ", invalid)}. Channel ids start with UC and are 24 characters.");
        var apiKeyConfigured = !string.IsNullOrWhiteSpace(options.Value.ApiKey);
        if (settings.Enabled && !apiKeyConfigured)
            throw new InvalidOperationException("Set YouTubeData:ApiKey in the web configuration before enabling YouTube episodes.");
        if (settings.Enabled && channels.Count == 0)
            throw new InvalidOperationException("Add at least one official channel id before enabling YouTube episodes.");

        var row = await GetOrCreateAsync(cancellationToken);
        row.Enabled = settings.Enabled;
        row.ChannelAllowlist = string.Join('\n', channels);
        row.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<DirectVideoSettingsEntity> GetOrCreateAsync(CancellationToken cancellationToken)
    {
        var row = await db.DirectVideoSettings.FirstOrDefaultAsync(x => x.IsSingleton, cancellationToken);
        if (row is not null) return row;
        row = new DirectVideoSettingsEntity();
        db.DirectVideoSettings.Add(row);
        await db.SaveChangesAsync(cancellationToken);
        return row;
    }

    private static List<string> Parse(string? text) => (text ?? string.Empty)
        .Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
}
