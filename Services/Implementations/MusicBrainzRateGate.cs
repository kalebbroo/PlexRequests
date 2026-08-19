using System.Diagnostics;

namespace PlexRequestsHosted.Services.Implementations;

public interface IMusicBrainzRateGate
{
    ValueTask WaitAsync(CancellationToken cancellationToken = default);
}

/// <summary>Process-wide request gate. MusicBrainz asks clients to average no more than one call per
/// second; a singleton gate keeps concurrent users and background refreshes inside that budget.</summary>
public sealed class MusicBrainzRateGate : IMusicBrainzRateGate
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(1100);
    private readonly SemaphoreSlim _mutex = new(1, 1);
    private long _nextAllowed;

    public async ValueTask WaitAsync(CancellationToken cancellationToken = default)
    {
        await _mutex.WaitAsync(cancellationToken);
        try
        {
            var now = Stopwatch.GetTimestamp();
            var remaining = _nextAllowed - now;
            if (remaining > 0)
                await Task.Delay(TimeSpan.FromSeconds((double)remaining / Stopwatch.Frequency), cancellationToken);
            _nextAllowed = Stopwatch.GetTimestamp() + (long)(Interval.TotalSeconds * Stopwatch.Frequency);
        }
        finally { _mutex.Release(); }
    }
}
