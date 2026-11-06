using System.Buffers.Binary;
using System.Text.Json;

namespace PeerFerry.Core;

internal static class Wire
{
    public const int MaximumFrame = 8 * 1024 * 1024;
    public static async Task WriteAsync<T>(Stream stream, T value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, StateStore.Json);
        if (bytes.Length > MaximumFrame) throw new InvalidDataException("Сообщение слишком велико.");
        var header = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(header, bytes.Length);
        await stream.WriteAsync(header, token); await stream.WriteAsync(bytes, token);
    }
    public static async Task<T> ReadAsync<T>(Stream stream, CancellationToken token, int maximumLength = MaximumFrame)
    {
        var header = new byte[4]; await stream.ReadExactlyAsync(header, token);
        var length = BinaryPrimitives.ReadInt32BigEndian(header);
        if (length < 1 || length > maximumLength) throw new InvalidDataException("Некорректная длина сообщения.");
        var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, token);
        return JsonSerializer.Deserialize<T>(bytes, StateStore.Json) ?? throw new InvalidDataException("Пустое сообщение.");
    }
    public static async Task BridgeAsync(Stream local, Stream remote, CancellationToken token, BandwidthLimiter upload, BandwidthLimiter download)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outgoing = CopyLimitedAsync(local, remote, upload, linked.Token);
        var incoming = CopyLimitedAsync(remote, local, download, linked.Token);
        await Task.WhenAny(outgoing, incoming);
        await linked.CancelAsync();
        try { await Task.WhenAll(outgoing, incoming); } catch (OperationCanceledException) { }
    }
    private static async Task CopyLimitedAsync(Stream source, Stream destination, BandwidthLimiter limiter, CancellationToken token)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(128 * 1024);
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory(), token)) != 0)
            { await limiter.WaitAsync(count, token); await destination.WriteAsync(buffer.AsMemory(0, count), token); }
        }
        finally { System.Buffers.ArrayPool<byte>.Shared.Return(buffer); }
    }
}

internal sealed record Challenge(string Nonce, string Id);
internal sealed record Hello(PeerDescriptor Peer, string GroupId, string Proof);
internal sealed record Welcome(string Proof, PeerDescriptor Peer);
internal sealed record Request(string Command, string? Hash = null);
internal sealed record Response(bool Ok, string? Error = null);
internal sealed record CatalogResponse(PeerDescriptor Peer, List<PeerDescriptor> Peers, List<CatalogEntry> Files);
