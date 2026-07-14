namespace PlexRequests.Downloader.Organize;

/// <summary>One file the organizer placed into the library — mirrors <see cref="PlexRequestsHosted.Shared.DTOs.ImportedFileDto"/>
/// but keeps this namespace free of a direct DTO dependency at the call sites that build it incrementally.</summary>
public record ImportedFileRecord(string SourcePath, string DestinationPath, string FileType, int? Season, int? Episode, long SizeBytes);

/// <summary>Outcome of importing one torrent's payload into the library.</summary>
public record ImportResult(bool Success, int VideoFileCount, string? FailReason, IReadOnlyList<ImportedFileRecord> Files)
{
    public static ImportResult Fail(string reason) => new(false, 0, reason, Array.Empty<ImportedFileRecord>());
    public static ImportResult Ok(IReadOnlyList<ImportedFileRecord> files) =>
        new(true, files.Count(f => f.FileType == "video"), null, files);
}
