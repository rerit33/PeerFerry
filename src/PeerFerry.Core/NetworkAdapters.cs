using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace PeerFerry.Core;

public sealed record NetworkBinding(string AdapterId, string Name, string Description, IPAddress Address, int PrefixLength, int Index, bool IsVpn)
{
    public bool Contains(IPAddress remote)
    {
        var a = Address.GetAddressBytes(); var b = remote.GetAddressBytes();
        if (a.Length != b.Length) return false;
        for (var i = 0; i < PrefixLength; i++) if ((a[i / 8] & (1 << (7 - i % 8))) != (b[i / 8] & (1 << (7 - i % 8)))) return false;
        return true;
    }
    public IPAddress Broadcast
    {
        get
        {
            var bytes = Address.GetAddressBytes();
            for (var i = PrefixLength; i < 32; i++) bytes[i / 8] |= (byte)(1 << (7 - i % 8));
            return new IPAddress(bytes);
        }
    }
}

public static class NetworkAdapters
{
    public static IReadOnlyList<NetworkBinding> Enumerate() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses
            .Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address) && a.Address.GetAddressBytes()[0] != 169)
            .Select(a => new NetworkBinding(n.Id, n.Name, n.Description, a.Address, a.PrefixLength, n.GetIPProperties().GetIPv4Properties()?.Index ?? 0,
                (n.Name + " " + n.Description).Contains("zerotier", StringComparison.OrdinalIgnoreCase) || (n.Name + " " + n.Description).Contains("radmin", StringComparison.OrdinalIgnoreCase))))
        .OrderByDescending(n => n.IsVpn).ThenBy(n => n.Name).ToArray();

    public static IReadOnlyList<NetworkBinding> Select(AppSettings settings, IReadOnlyList<NetworkBinding> available) => available
        .Where(n => settings.NetworkMode == "All" || (settings.NetworkMode == "Manual" ? settings.AdapterIds.Contains(n.AdapterId) : n.IsVpn)).ToArray();

    public static void Bind(Socket socket, NetworkBinding binding)
    {
        socket.Bind(new IPEndPoint(binding.Address, 0));
        // Windows otherwise may route a socket with a bound source address through another NIC.
        if (OperatingSystem.IsWindows() && binding.Index > 0)
            socket.SetSocketOption(SocketOptionLevel.IP, (SocketOptionName)31 /* IP_UNICAST_IF */, IPAddress.HostToNetworkOrder(binding.Index));
    }
}
