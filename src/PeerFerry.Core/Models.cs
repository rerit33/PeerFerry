using System.Security.Cryptography;

namespace PeerFerry.Core;

public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    public string DisplayName { get; set; } = Environment.MachineName;
    public string GroupName { get; set; } = "Друзья";
    public string GroupKey { get; set; } = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
    public string NetworkMode { get; set; } = "Auto";
    public List<string> AdapterIds { get; set; } = [];
    public int Port { get; set; } = 45831;
    public string DownloadDirectory { get; set; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "PeerFerry");
    public int DownloadLimitKiB { get; set; }
    public int UploadLimitKiB { get; set; }
    public int ConcurrentDownloads { get; set; } = 3;
    public bool MinimizeToTray { get; set; } = true;
    public bool AutoCheckUpdates { get; set; } = true;
    public string UpdateRepository { get; set; } = "https://github.com/rerit33/PeerFerry";
    public List<PeerDescriptor> KnownPeers { get; set; } = [];
    public List<ShareRecord> Shares { get; set; } = [];
    public string GroupId => Crypto.GroupId(GroupKey);
}

public sealed record PeerEndpoint(string Address, int Port);
public sealed record PeerDescriptor(string Id, string Name, List<PeerEndpoint> Endpoints);
public sealed record Invitation(int Version, string GroupName, string GroupKey, PeerDescriptor Peer);
public sealed record CatalogEntry(string Hash, string Name, long Size, int FileCount, string OwnerId, string OwnerName, double? Progress = null);
public sealed record RemoteShare(CatalogEntry Entry, List<PeerDescriptor> Sources);
public sealed record SourceStamp(string Path, long Length, long ModifiedTicks);

public sealed class ShareRecord
{
    public string Hash { get; set; } = "";
    public string GroupId { get; set; } = "";
    public string Name { get; set; } = "";
    public string MetadataFile { get; set; } = "";
    public string SaveDirectory { get; set; } = "";
    public bool IsPublication { get; set; }
    public bool Paused { get; set; }
    public List<SourceStamp> Sources { get; set; } = [];
}

public sealed record TransferSnapshot(string Hash, string Name, long Size, double Progress,
    long DownloadSpeed, long UploadSpeed, int Connections, string State, string Directory, bool IsPublication,
    long CompletedBytes = 0, int FileCount = 1);

public sealed record ShareFileSnapshot(string Path, long Size, double? Progress);
public sealed record TorrentConnectionSnapshot(int LocalPort, long DownloadSpeed, long UploadSpeed, double Progress);
public sealed record ShareParticipantSnapshot(string Id, string Name, long DownloadSpeed, long UploadSpeed,
    double? Progress, bool Online, bool Connected);

public sealed record PeerSnapshot(string Id, string Name, string Addresses, bool Online, DateTimeOffset LastSeen, string Error);

public static class Crypto
{
    public static string GroupId(string key) => Convert.ToHexString(SHA256.HashData(Convert.FromBase64String(key)))[..32];
    public static string Mac(string key, string value) => Convert.ToBase64String(HMACSHA256.HashData(Convert.FromBase64String(key), System.Text.Encoding.UTF8.GetBytes(value)));
    public static bool Verify(string key, string value, string proof)
    {
        try { return CryptographicOperations.FixedTimeEquals(Convert.FromBase64String(Mac(key, value)), Convert.FromBase64String(proof)); }
        catch (FormatException) { return false; }
    }
}
