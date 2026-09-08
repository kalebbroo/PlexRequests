using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PlexRequests.Downloader.Organize;

public interface IMultipartEpisodeJoiner
{
    /// <summary>Losslessly append ordered MKV parts, prepare the private output, then atomically replace dest.</summary>
    Task<long> JoinAsync(IReadOnlyList<string> sourceParts, string destination,
        Action<string>? prepareStaged, CancellationToken ct);
}

public sealed class MkvMergeEpisodeJoiner(ILogger<MkvMergeEpisodeJoiner> logger) : IMultipartEpisodeJoiner
{
    public async Task<long> JoinAsync(IReadOnlyList<string> sourceParts, string destination,
        Action<string>? prepareStaged, CancellationToken ct)
    {
        if (sourceParts.Count is < 2 or > 8) throw new ArgumentException("A join requires 2 through 8 parts.");
        if (sourceParts.Any(path => !File.Exists(path)
                                    || !Path.GetExtension(path).Equals(".mkv", StringComparison.OrdinalIgnoreCase)))
            throw new ArgumentException("Every join part must be an existing MKV file.");
        var directory = Path.GetDirectoryName(destination)
                        ?? throw new IOException("Joined episode has no destination directory.");
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory,
            $".{Path.GetFileName(destination)}.plexrequests-join-{Guid.NewGuid():N}.partial");
        try
        {
            var start = new ProcessStartInfo
            {
                FileName = "mkvmerge",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            start.Environment["LANG"] = "C.UTF-8";
            start.Environment["LC_ALL"] = "C.UTF-8";
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(staging);
            for (var index = 0; index < sourceParts.Count; index++)
            {
                if (index > 0) start.ArgumentList.Add("+");
                start.ArgumentList.Add(sourceParts[index]);
            }
            using var process = new Process { StartInfo = start };
            if (!process.Start()) throw new IOException("mkvmerge did not start.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromHours(2));
            var stdout = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderr = process.StandardError.ReadToEndAsync(timeout.Token);
            try
            {
                await process.WaitForExitAsync(timeout.Token);
            }
            catch (OperationCanceledException)
            {
                TryKill(process);
                throw;
            }
            var output = await stdout;
            var error = await stderr;
            if (process.ExitCode > 1)
                throw new IOException("mkvmerge failed: " + Clean(error.Length > 0 ? error : output));
            if (!File.Exists(staging) || new FileInfo(staging).Length <= 0)
                throw new IOException("mkvmerge produced no data.");

            prepareStaged?.Invoke(staging);
            var preparedLength = new FileInfo(staging).Length;
            if (preparedLength <= 0) throw new IOException("The prepared joined episode is empty.");
            File.Move(staging, destination, overwrite: true);
            var committedLength = new FileInfo(destination).Length;
            if (committedLength != preparedLength)
                throw new IOException($"Joined episode commit was short: expected {preparedLength}, found {committedLength}.");
            return committedLength;
        }
        finally
        {
            if (File.Exists(staging))
            {
                try { File.Delete(staging); }
                catch (Exception ex) { logger.LogDebug(ex, "Could not remove failed join staging file {Path}", staging); }
            }
        }
    }

    private static string Clean(string value)
    {
        var clean = Regex.Replace(value ?? string.Empty, @"\s+", " ").Trim();
        return clean.Length <= 800 ? clean : clean[..800];
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch
        {
            // The original cancellation/timeout is more useful than a cleanup failure.
        }
    }
}
