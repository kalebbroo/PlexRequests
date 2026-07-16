using System.ComponentModel.DataAnnotations;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Infrastructure.Entities;

/// <summary>
/// An admin-configured NAS / network drive (SMB or NFS). Both containers mount it at
/// <c>/mnt/nas/{MountSlug}</c> — the web app read-only (for the folder browser), the downloader
/// read-write (to place files) — so paths configured in Library Organization resolve in both.
/// The password is stored encrypted (ASP.NET DataProtection); it is never returned to the browser
/// and is only decrypted server-side when building a mount command or serving the downloader's
/// secured mount-config endpoint.
/// </summary>
public class NetworkShareEntity
{
    [Key]
    public int Id { get; set; }

    [MaxLength(128)] public string Name { get; set; } = string.Empty;

    /// <summary>Stable, path-safe slug generated from the name at creation and never changed
    /// (so a rename doesn't move the mount path out from under configured library paths).</summary>
    [MaxLength(64)] public string MountSlug { get; set; } = string.Empty;

    public NetworkShareProtocol Protocol { get; set; } = NetworkShareProtocol.Smb;

    [MaxLength(255)] public string Server { get; set; } = string.Empty;
    [MaxLength(255)] public string ShareName { get; set; } = string.Empty;
    [MaxLength(128)] public string? Domain { get; set; }
    [MaxLength(128)] public string? Username { get; set; }

    /// <summary>DataProtection-encrypted password blob (base64). Null = no password (guest/anonymous).</summary>
    public string? PasswordProtected { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
}
