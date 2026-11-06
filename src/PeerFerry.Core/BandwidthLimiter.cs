using System.Diagnostics;

namespace PeerFerry.Core;

internal sealed class BandwidthLimiter(int kibibytesPerSecond)
{
    private readonly SemaphoreSlim gate = new(1);
    private readonly long bytesPerSecond = (long)kibibytesPerSecond * 1024;
    private double next;
    public async ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        if (bytesPerSecond == 0) return;
        await gate.WaitAsync(token);
        try
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            var deadline = Math.Max(next, now) + bytes / (double)bytesPerSecond;
            await Task.Delay(TimeSpan.FromSeconds(deadline - now), token);
            // Cancelled/paused connections do not reserve future bandwidth for other peers.
            next = deadline;
        }
        finally { gate.Release(); }
    }
}
