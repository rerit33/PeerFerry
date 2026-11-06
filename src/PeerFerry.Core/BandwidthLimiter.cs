using System.Diagnostics;

namespace PeerFerry.Core;

internal sealed class BandwidthLimiter(int kibibytesPerSecond)
{
    private readonly object gate = new();
    private readonly long bytesPerSecond = (long)kibibytesPerSecond * 1024;
    private double next;
    public ValueTask WaitAsync(int bytes, CancellationToken token)
    {
        if (bytesPerSecond == 0) return ValueTask.CompletedTask;
        double delay;
        lock (gate)
        {
            var now = Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;
            next = Math.Max(next, now) + bytes / (double)bytesPerSecond;
            delay = next - now;
        }
        return new(Task.Delay(TimeSpan.FromSeconds(delay), token));
    }
}
