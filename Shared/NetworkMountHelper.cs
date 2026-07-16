using System.Diagnostics;
using PlexRequestsHosted.Shared.DTOs;
using PlexRequestsHosted.Shared.Enums;

namespace PlexRequestsHosted.Shared;

/// <summary>Outcome of reconciling one share's mount.</summary>
public sealed record NetworkMountResult(string MountSlug, bool Mounted, string? Error);

/// <summary>
/// Mounts admin-configured SMB/NFS network shares into the container at <c>/mnt/nas/{slug}</c>, and
/// reconciles the live OS mount table to a desired set (mount new/changed, unmount removed/disabled).
///
/// Used by BOTH containers: the web app mounts read-only (for the folder browser) and the downloader
/// mounts read-write (to place files) — at the same path, so Library Organization paths are valid in
/// both. Requires the container to have the CAP_SYS_ADMIN capability and the cifs-utils / nfs-common
/// helper binaries (both provided via docker-compose + the Dockerfiles). Any failure (missing
/// capability, unreachable server, bad credentials) is captured and returned per-share rather than
/// thrown, so a bad share never takes down the process.
/// </summary>
public static class NetworkMountHelper
{
    /// <summary>Root under which every share is mounted, in both containers.</summary>
    public const string MountRoot = "/mnt/nas";

    public static string MountPathFor(string slug) => $"{MountRoot}/{slug}";

    /// <summary>
    /// Make the OS mount table under <see cref="MountRoot"/> match <paramref name="configs"/>: mount
    /// enabled shares that aren't mounted (or whose connection details changed), and unmount anything
    /// under the root that's no longer wanted. Never throws; returns one result per enabled share.
    /// </summary>
    /// <param name="readOnly">Mount read-only (web app) vs read-write (downloader).</param>
    /// <param name="uid">Owner uid for files on SMB mounts (so Plex can read them). Ignored for NFS.</param>
    public static async Task<IReadOnlyList<NetworkMountResult>> ReconcileAsync(
        IReadOnlyList<NetworkShareMountDto> configs, bool readOnly, int uid, int gid,
        Action<string>? log, CancellationToken ct)
    {
        var results = new List<NetworkMountResult>();

        if (!OperatingSystem.IsLinux())
        {
            // Network mounts are a Linux/Docker concern; on a dev box we simply report "not mounted".
            foreach (var c in configs.Where(c => c.Enabled))
                results.Add(new(c.MountSlug, false, "Network shares can only be mounted on the Linux/Docker deployment."));
            return results;
        }

        try { Directory.CreateDirectory(MountRoot); }
        catch (Exception ex)
        {
            foreach (var c in configs.Where(c => c.Enabled))
                results.Add(new(c.MountSlug, false, $"Cannot create {MountRoot}: {ex.Message}"));
            return results;
        }

        var enabled = configs.Where(c => c.Enabled && !string.IsNullOrWhiteSpace(c.MountSlug)).ToList();
        var desired = enabled.ToDictionary(c => MountPathFor(c.MountSlug), c => c, StringComparer.Ordinal);
        var current = ReadCurrentMounts();

        // Unmount anything under the root that's no longer wanted, or whose device (server/share)
        // changed — the admin edited the connection, so the stale mount must be replaced.
        foreach (var (mountpoint, device) in current)
        {
            var stale = !desired.TryGetValue(mountpoint, out var want) ||
                        !string.Equals(device, DeviceFor(want), StringComparison.OrdinalIgnoreCase);
            if (!stale) continue;
            log?.Invoke($"Unmounting stale/removed network share at {mountpoint}");
            await RunAsync("umount", new[] { mountpoint }, ct);
        }

        current = ReadCurrentMounts(); // refresh after unmounts

        foreach (var c in enabled)
        {
            var target = MountPathFor(c.MountSlug);
            if (current.TryGetValue(target, out var dev) && string.Equals(dev, DeviceFor(c), StringComparison.OrdinalIgnoreCase))
            {
                results.Add(new(c.MountSlug, true, null)); // already mounted correctly
                continue;
            }
            results.Add(await MountOneAsync(c, target, readOnly, uid, gid, log, ct));
        }

        return results;
    }

    /// <summary>Unmount every share under the root (best-effort) — for graceful shutdown.</summary>
    public static async Task UnmountAllAsync(CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux()) return;
        foreach (var (mountpoint, _) in ReadCurrentMounts())
            await RunAsync("umount", new[] { mountpoint }, ct);
    }

    private static async Task<NetworkMountResult> MountOneAsync(
        NetworkShareMountDto c, string target, bool readOnly, int uid, int gid, Action<string>? log, CancellationToken ct)
    {
        try { Directory.CreateDirectory(target); }
        catch (Exception ex) { return new(c.MountSlug, false, $"Cannot create mount point: {ex.Message}"); }

        string? credFile = null;
        try
        {
            var device = DeviceFor(c);
            var (fsType, options) = c.Protocol == NetworkShareProtocol.Nfs
                ? ("nfs", NfsOptions(readOnly))
                : ("cifs", SmbOptions(c, readOnly, uid, gid, out credFile));

            log?.Invoke($"Mounting {device} -> {target} ({fsType}, {(readOnly ? "ro" : "rw")})");
            var (ok, stderr) = await RunAsync("mount",
                new[] { "-t", fsType, device, target, "-o", options }, ct);

            if (ok) return new(c.MountSlug, true, null);
            return new(c.MountSlug, false, CleanError(stderr));
        }
        catch (Exception ex)
        {
            return new(c.MountSlug, false, ex.Message);
        }
        finally
        {
            if (credFile is not null) { try { File.Delete(credFile); } catch { /* best-effort */ } }
        }
    }

    /// <summary>SMB device string, e.g. <c>//192.168.1.10/media</c>.</summary>
    private static string DeviceFor(NetworkShareMountDto c) => c.Protocol == NetworkShareProtocol.Nfs
        ? $"{c.Server}:{NormalizeExport(c.ShareName)}"
        : $"//{c.Server}/{c.ShareName.Trim().Trim('/')}";

    private static string NormalizeExport(string export)
    {
        var e = export.Trim();
        return e.StartsWith('/') ? e : "/" + e;
    }

    private static string NfsOptions(bool readOnly) =>
        string.Join(',', new[] { readOnly ? "ro" : "rw", "soft", "timeo=30", "retrans=2" });

    private static string SmbOptions(NetworkShareMountDto c, bool readOnly, int uid, int gid, out string? credFile)
    {
        var opts = new List<string>
        {
            readOnly ? "ro" : "rw",
            $"uid={uid}", $"gid={gid}",
            "iocharset=utf8", "file_mode=0664", "dir_mode=0775"
        };

        credFile = null;
        if (string.IsNullOrWhiteSpace(c.Username))
        {
            opts.Add("guest");
        }
        else
        {
            // Pass credentials via a 0600 temp file rather than on the command line, so the password
            // never appears in the process list / /proc/<pid>/cmdline.
            credFile = WriteCredentialsFile(c);
            opts.Add($"credentials={credFile}");
        }
        return string.Join(',', opts);
    }

    private static string WriteCredentialsFile(NetworkShareMountDto c)
    {
        var path = Path.Combine(Path.GetTempPath(), $"smbcred-{c.MountSlug}-{Guid.NewGuid():N}");
        var lines = new List<string> { $"username={c.Username}", $"password={c.Password}" };
        if (!string.IsNullOrWhiteSpace(c.Domain)) lines.Add($"domain={c.Domain}");
        File.WriteAllLines(path, lines);
        try { File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite); } catch { /* best-effort */ }
        return path;
    }

    /// <summary>Parse /proc/mounts into mountpoint -> device, restricted to entries under the root.</summary>
    private static Dictionary<string, string> ReadCurrentMounts()
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var line in File.ReadLines("/proc/mounts"))
            {
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;
                var mountpoint = Unescape(parts[1]);
                if (mountpoint.StartsWith(MountRoot + "/", StringComparison.Ordinal))
                    map[mountpoint] = parts[0];
            }
        }
        catch { /* /proc unavailable off-Linux — caller already guarded */ }
        return map;
    }

    // /proc/mounts octal-escapes spaces/tabs; our slugs are path-safe but be correct anyway.
    private static string Unescape(string s) => s
        .Replace("\\040", " ").Replace("\\011", "\t").Replace("\\012", "\n").Replace("\\134", "\\");

    private static string CleanError(string stderr)
    {
        var msg = stderr.Trim();
        if (string.IsNullOrEmpty(msg)) return "mount failed (no error output)";
        // Surface the most actionable line (usually the last).
        var last = msg.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault();
        return (last ?? msg).Length > 300 ? (last ?? msg)[..300] : (last ?? msg);
    }

    private static async Task<(bool ok, string stderr)> RunAsync(string file, string[] args, CancellationToken ct)
    {
        var psi = new ProcessStartInfo(file)
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        try
        {
            using var proc = Process.Start(psi);
            if (proc is null) return (false, $"could not start '{file}'");

            // Cap the wait: an unreachable server can make mount hang for a long time.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var stderrTask = proc.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await proc.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                try { proc.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return (false, "mount timed out (server unreachable?)");
            }
            var stderr = await stderrTask;
            return (proc.ExitCode == 0, stderr);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }
}
