using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace PeerFerry.Core;

internal sealed class DiscoveryService(string key, Func<PeerDescriptor> self, IReadOnlyList<NetworkBinding> bindings,
    Action<PeerDescriptor> found, Action<string> notice) : IAsyncDisposable
{
    public const int Port = 45832;
    private static readonly IPAddress Group = IPAddress.Parse("239.255.84.71");
    private readonly CancellationTokenSource lifetime = new();
    private Socket? receiver;
    private Task? receiveTask;
    private Task? sendTask;
    private sealed record Payload(string GroupId, long Time, PeerDescriptor Peer);
    private sealed record Beacon(string Payload, string Proof);
    public void Start()
    {
        try
        {
            receiver = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            receiver.ExclusiveAddressUse = false;
            receiver.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.PacketInformation, true);
            receiver.Bind(new IPEndPoint(IPAddress.Any, Port));
            foreach (var binding in bindings)
            {
                try { receiver.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership, new MulticastOption(Group, binding.Address)); }
                catch (SocketException) { /* Broadcast and invitations still work on this interface. */ }
            }
            receiveTask = Task.Run(ReceiveAsync);
            sendTask = Task.Run(SendAsync);
        }
        catch (SocketException e) { receiver?.Dispose(); notice($"Поиск в сети недоступен: {e.Message}. Используйте приглашение."); }
    }
    private async Task SendAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var payload = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(new Payload(Crypto.GroupId(key), DateTimeOffset.UtcNow.ToUnixTimeSeconds(), self()), StateStore.Json));
                var packet = JsonSerializer.SerializeToUtf8Bytes(new Beacon(payload, Crypto.Mac(key, payload)), StateStore.Json);
                foreach (var binding in bindings)
                {
                    try
                    {
                        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                        NetworkAdapters.Bind(socket, binding); socket.EnableBroadcast = true;
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastInterface, binding.Address.GetAddressBytes());
                        socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.MulticastTimeToLive, 1);
                        await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(Group, Port), lifetime.Token);
                        if (binding.PrefixLength is > 0 and < 31)
                            await socket.SendToAsync(packet, SocketFlags.None, new IPEndPoint(binding.Broadcast, Port), lifetime.Token);
                    }
                    catch (SocketException) { }
                }
                await Task.Delay(TimeSpan.FromSeconds(5), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    private async Task ReceiveAsync()
    {
        var buffer = new byte[8192];
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var result = await receiver!.ReceiveMessageFromAsync(buffer.AsMemory(), SocketFlags.None, new IPEndPoint(IPAddress.Any, 0), lifetime.Token);
                // The UDP receiver is wildcard-bound for multicast; reject packets from unchecked NICs.
                if (!bindings.Any(b => b.Index == result.PacketInformation.Interface)) continue;
                try
                {
                    var beacon = JsonSerializer.Deserialize<Beacon>(buffer.AsSpan(0, result.ReceivedBytes), StateStore.Json);
                    if (beacon is null || !Crypto.Verify(key, beacon.Payload, beacon.Proof)) continue;
                    var payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(beacon.Payload), StateStore.Json);
                    if (payload is null || payload.GroupId != Crypto.GroupId(key) || Math.Abs(DateTimeOffset.UtcNow.ToUnixTimeSeconds() - payload.Time) > 120 || payload.Peer.Id == self().Id) continue;
                    Invitations.ValidatePeer(payload.Peer); found(payload.Peer);
                }
                catch (Exception e) when (e is JsonException or FormatException or InvalidDataException or NullReferenceException or OverflowException) { }
            }
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); receiver?.Dispose();
        if (receiveTask is not null) await receiveTask;
        if (sendTask is not null) await sendTask;
        lifetime.Dispose();
    }
}
