namespace PlexRequests.Downloader.Download;

/// <summary>Status of a torrent in the download client.</summary>
/// <param name="Name">The torrent's display name — sourced from the magnet's dn= hint or the resolved
/// .torrent metadata. NOT reliable as an on-disk path component: a magnet's dn= is only a hint and can
/// differ from the actual file/folder name once the torrent resolves. Use <paramref name="Files"/> to
/// find the real on-disk path; Name is a display-only fallback when Files is unavailable.</param>
/// <param name="Files">Relative-to-SavePath paths of every file in the torrent, as reported by the
/// download client — the authoritative source for where content actually landed on disk. Empty if the
/// client doesn't report it (e.g. an older API) or the torrent metadata isn't resolved yet.</param>
public record DownloadStatus(string State, double Progress, string Name, long TotalSizeBytes, string? SavePath, bool IsFinished, IReadOnlyList<string> Files);

/// <summary>Torrent client abstraction (implemented for Deluge; swappable).</summary>
public interface IDownloadClient
{
    /// <summary>Add a magnet, optionally labelled for library routing. Returns the torrent id/hash, or null on failure.</summary>
    Task<string?> AddMagnetAsync(string magnet, string? label, CancellationToken ct);
    Task<DownloadStatus?> GetStatusAsync(string torrentId, CancellationToken ct);
    Task<bool> RemoveAsync(string torrentId, bool removeData, CancellationToken ct);
    /// <summary>Restrict which files of a multi-file torrent actually download. <paramref name="keep"/> is
    /// indexed to match the file order reported by <see cref="DownloadStatus.Files"/> (true = download,
    /// false = skip). Used to pull only the wanted episodes out of a season pack. Best-effort — returns
    /// false if the client can't apply it (metadata not yet resolved, plugin/option unsupported).</summary>
    Task<bool> SetWantedFilesAsync(string torrentId, IReadOnlyList<bool> keep, CancellationToken ct);
}
