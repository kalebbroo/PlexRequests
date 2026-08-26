namespace PlexRequests.Downloader.Organize;

using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

/// <summary>One file the organizer placed into the library — mirrors <see cref="PlexRequestsHosted.Shared.DTOs.ImportedFileDto"/>
/// but keeps this namespace free of a direct DTO dependency at the call sites that build it incrementally.</summary>
public record ImportedFileRecord(string SourcePath, string DestinationPath, string FileType, int? Season,
    int? Episode, long SizeBytes, MediaTrackSummaryDto? MediaTracks = null,
    IReadOnlyList<EpisodeRef>? EpisodeCoverage = null);

/// <summary>Outcome of importing one torrent's payload into the library.</summary>
public record ImportResult(bool Success, int MediaFileCount, string? FailReason,
    IReadOnlyList<ImportedFileRecord> Files, BlocklistReason? BlocklistReason = null)
{
    public static ImportResult Fail(string reason, BlocklistReason? blocklistReason = null) =>
        new(false, 0, reason, Array.Empty<ImportedFileRecord>(), blocklistReason);
    public static ImportResult Ok(IReadOnlyList<ImportedFileRecord> files) =>
        new(true, files.Count(f => f.FileType is "video" or "audio"), null, files);
}
