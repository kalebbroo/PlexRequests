using PlexRequests.Downloader.Organize;

namespace PlexRequests.Tests;

internal sealed class TestMultipartEpisodeJoiner : IMultipartEpisodeJoiner
{
    public async Task<long> JoinAsync(IReadOnlyList<string> sourceParts, string destination,
        Action<string>? prepareStaged, CancellationToken ct)
    {
        var directory = Path.GetDirectoryName(destination)!;
        Directory.CreateDirectory(directory);
        var staging = Path.Combine(directory, $".{Path.GetFileName(destination)}.test.partial");
        try
        {
            await using (var output = File.Create(staging))
            foreach (var source in sourceParts)
            {
                await using var input = File.OpenRead(source);
                await input.CopyToAsync(output, ct);
            }
            prepareStaged?.Invoke(staging);
            File.Move(staging, destination, overwrite: true);
            return new FileInfo(destination).Length;
        }
        finally
        {
            if (File.Exists(staging)) File.Delete(staging);
        }
    }
}
