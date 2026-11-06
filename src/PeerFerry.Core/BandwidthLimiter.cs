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
            // Carry up to 50 ms of unused credit so timer granularity does not
            // reduce the configured rate on small TLS records. Idle bursts stay bounded.
            var deadline = Math.Max(next, now - 0.05) + bytes / (double)bytesPerSecond;
            if (deadline > now) await Task.Delay(TimeSpan.FromSeconds(deadline - now), token);
            // Cancelled/paused connections do not reserve future bandwidth for other peers.
            next = deadline;
        }
        finally { gate.Release(); }
    }
}
