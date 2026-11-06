using System.Net;
using System.Text.Json;

namespace PeerFerry.Core;

public static class Invitations
{
    public const string Prefix = "peerferry://join/";
    public static string Encode(Invitation invitation) => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(invitation, StateStore.Json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    public static Invitation Decode(string text)
    {
        text = text.Trim();
        if (!text.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase) || text.Length > 16_384)
            throw new InvalidDataException("Вставьте приглашение peerferry://join/…");
        try
        {
            var encoded = text[Prefix.Length..].Replace('-', '+').Replace('_', '/');
            encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
            var value = JsonSerializer.Deserialize<Invitation>(Convert.FromBase64String(encoded), StateStore.Json)
                ?? throw new InvalidDataException("Пустое приглашение.");
            if (value.Version != 1 || Convert.FromBase64String(value.GroupKey).Length != 32 || value.GroupName.Length > 80)
                throw new InvalidDataException("Неподдерживаемое приглашение.");
            ValidatePeer(value.Peer);
            return value;
        }
        catch (Exception e) when (e is FormatException or JsonException or NullReferenceException)
        { throw new InvalidDataException("Приглашение повреждено.", e); }
    }
    public static void ValidatePeer(PeerDescriptor peer)
    {
        if (peer is null || peer.Id is null || peer.Id.Length != 64 || !peer.Id.All(Uri.IsHexDigit) || string.IsNullOrWhiteSpace(peer.Name) || peer.Name.Length > 80 || peer.Endpoints is null || peer.Endpoints.Count > 24)
            throw new InvalidDataException("Некорректные данные участника.");
        foreach (var ep in peer.Endpoints)
        {
            if (ep is null || !IPAddress.TryParse(ep.Address, out var ip) || ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork ||
                ep.Port is < 1024 or > 65535 || ip.Equals(IPAddress.Any) || ip.GetAddressBytes()[0] is >= 224 or 0)
                throw new InvalidDataException("Некорректный сетевой адрес участника.");
        }
    }
}
