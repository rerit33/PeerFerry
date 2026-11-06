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
    public static async Task BridgeAsync(Stream first, Stream second, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token);
        var outgoing = first.CopyToAsync(second, 128 * 1024, linked.Token);
        var incoming = second.CopyToAsync(first, 128 * 1024, linked.Token);
        await Task.WhenAny(outgoing, incoming);
        await linked.CancelAsync();
        try { await Task.WhenAll(outgoing, incoming); } catch (OperationCanceledException) { }
    }
}

internal sealed record Challenge(string Nonce, string Id);
internal sealed record Hello(PeerDescriptor Peer, string GroupId, string Proof);
internal sealed record Welcome(string Proof, PeerDescriptor Peer);
internal sealed record Request(string Command, string? Hash = null);
internal sealed record Response(bool Ok, string? Error = null);
internal sealed record CatalogResponse(PeerDescriptor Peer, List<PeerDescriptor> Peers, List<CatalogEntry> Files);
