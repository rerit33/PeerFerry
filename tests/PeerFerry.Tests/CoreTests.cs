using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using MonoTorrent.BEncoding;
using PeerFerry.Core;

namespace PeerFerry.Tests;

public sealed class CoreTests(Xunit.Abstractions.ITestOutputHelper output)
{
    [Fact]
    public void InvitationRoundTripAndMalformedInput()
    {
        var settings = new AppSettings();
        var peer = new PeerDescriptor(new string('A', 64), "Компьютер друга", [new("10.10.20.3", 45831)]);
        var invitation = Invitations.Decode(Invitations.Encode(new(1, "Друзья", settings.GroupKey, peer)));
        Assert.Equal(settings.GroupKey, invitation.GroupKey);
        Assert.Equal(peer.Id, invitation.Peer.Id);
        Assert.Throws<InvalidDataException>(() => Invitations.Decode("peerferry://join/garbage"));
        Assert.Throws<InvalidDataException>(() => Invitations.Decode(Invitations.Encode(new(99, "Друзья", settings.GroupKey, peer))));
        Assert.Throws<InvalidDataException>(() => Invitations.ValidatePeer(peer with { Endpoints = [new("https://evil.example", 443)] }));
    }
    [Fact]
    public void AdapterSelectionDoesNotFallBackToUnselectedNetworks()
    {
        NetworkBinding[] available = [new("vpn", "ZeroTier", "", IPAddress.Parse("10.2.0.1"), 24, 1, true),
            new("work", "Рабочий VPN", "", IPAddress.Parse("10.3.0.1"), 24, 2, false)];
        var settings = new AppSettings();
        Assert.Single(NetworkAdapters.Select(settings, available));
        settings.NetworkMode = "Manual"; settings.AdapterIds = ["missing"];
        Assert.Empty(NetworkAdapters.Select(settings, available));
        settings.AdapterIds = ["work"];
        Assert.Equal("work", Assert.Single(NetworkAdapters.Select(settings, available)).AdapterId);
        settings.NetworkMode = "All"; Assert.Equal(2, NetworkAdapters.Select(settings, available).Count);
        Assert.True(available[0].Contains(IPAddress.Parse("10.2.0.254")));
        Assert.False(available[0].Contains(IPAddress.Parse("10.3.0.1")));
    }
    [Theory]
    [InlineData("../secret")][InlineData("a/../../b")][InlineData("C:\\secret")]
    [InlineData("a:stream")][InlineData("CON.txt")][InlineData("a/LPT1")]
    [InlineData("a./file")][InlineData("a//file")]
    public void UnsafePathsAreRejected(string path) => Assert.Throws<InvalidDataException>(() => TorrentService.ValidateRelativePath(path));
    [Fact]
    public void GroupProofRejectsWrongKeyAndTampering()
    {
        var first = new AppSettings(); var other = new AppSettings();
        var proof = Crypto.Mac(first.GroupKey, "challenge");
        Assert.True(Crypto.Verify(first.GroupKey, "challenge", proof));
        Assert.False(Crypto.Verify(other.GroupKey, "challenge", proof));
        Assert.False(Crypto.Verify(first.GroupKey, "different", proof));
    }
    [Fact]
    public async Task AuthenticatedSwarmResumesAndRecipientSeedsWithoutOriginal()
    {
        var root = Path.Combine(Path.GetTempPath(), "PeerFerry-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var source = Path.Combine(root, "Архив с файлами.bin");
        var data = RandomNumberGenerator.GetBytes(16 * 1024 * 1024);
        await File.WriteAllBytesAsync(source, data);
        var firstSettings = Settings(root, "a");
        var secondSettings = Settings(root, "b", firstSettings.GroupKey); secondSettings.DownloadLimitKiB = 2048;
        var thirdSettings = Settings(root, "c", firstSettings.GroupKey);
        MeshNode? a = null, b = null, c = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(150));
        try
        {
            a = Node(root, "a", firstSettings); b = Node(root, "b", secondSettings);
            a.Diagnostic += output.WriteLine; b.Diagnostic += output.WriteLine;
            await a.StartAsync(); await b.StartAsync();
            var hash = await a.Torrents.PublishAsync(source, timeout.Token);
            // Metadata cannot introduce an Internet tracker or a different group.
            var clean = a.Torrents.Metadata(hash);
            var altered = BEncodedValue.Decode<BEncodedDictionary>(clean); altered["announce"] = new BEncodedString("https://tracker.invalid/announce");
            Assert.Throws<InvalidDataException>(() => TorrentService.ValidateMetadata(altered.Encode(), firstSettings.GroupId));
            Assert.Throws<InvalidDataException>(() => TorrentService.ValidateMetadata(clean, new AppSettings().GroupId));
            await b.RememberPeerAsync(Invitations.Decode(a.CreateInvitation()).Peer);
            await b.RefreshAsync(timeout.Token);
            Assert.True(b.PeerSnapshots().Any(p => p.Online), string.Join("; ", b.PeerSnapshots().Select(p => p.Error)));
            var remote = Assert.Single(b.Catalog());
            var preview = await b.GetMetadataAsync(remote, timeout.Token);
            var previewFile = Assert.Single(TorrentService.DescribeMetadata(preview, firstSettings.GroupId));
            Assert.Equal(data.Length, previewFile.Size); Assert.Null(previewFile.Progress);
            Assert.Empty(b.Torrents.Snapshot()); Assert.False(Directory.Exists(secondSettings.DownloadDirectory));
            Assert.Equal("a", Assert.Single(b.CatalogParticipants(hash)).Name);
            await b.DownloadAsync(remote, timeout.Token, preview);
            await UntilAsync(() => b.Torrents.Snapshot().Any(s => s.Progress is > 5 and < 100), timeout.Token);
            var partial = Assert.Single(b.Torrents.Snapshot());
            Assert.InRange(partial.CompletedBytes, 1, data.Length - 1);
            Assert.InRange(Assert.Single(b.Torrents.Files(hash)).Progress!.Value, 1, 99.99);
            var participant = Assert.Single(await b.TransferParticipantsAsync(hash), p => p.Connected);
            Assert.Equal(a.Id, participant.Id); Assert.Equal("a", participant.Name); Assert.Equal(100, participant.Progress);
            var inbound = Assert.Single(await a.TransferParticipantsAsync(hash), p => p.Connected);
            Assert.Equal(b.Id, inbound.Id); Assert.Equal("b", inbound.Name);
            await b.DisposeAsync(); b = null;
            var restoredStore = new StateStore(Path.Combine(root, "b"));
            b = new MeshNode(restoredStore, restoredStore.Load(), TestBindings, false);
            await b.StartAsync();
            Assert.Single(b.Torrents.Snapshot());
            Assert.True(b.Torrents.Snapshot()[0].Progress > 0);
            await UntilAsync(() => b.Torrents.Snapshot()[0].Progress >= 100, timeout.Token);
            Assert.Equal(data.Length, b.Torrents.Snapshot()[0].CompletedBytes);
            var secondPath = Directory.GetFiles(secondSettings.DownloadDirectory, "*.bin", SearchOption.AllDirectories).Single();
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(secondPath, timeout.Token)));
            await a.DisposeAsync(); a = null;
            c = Node(root, "c", thirdSettings); await c.StartAsync();
            await c.RememberPeerAsync(b.Descriptor); await c.RefreshAsync(timeout.Token);
            await c.DownloadAsync(Assert.Single(c.Catalog()), timeout.Token);
            await UntilAsync(() => c.Torrents.Snapshot()[0].Progress >= 100, timeout.Token);
            var thirdPath = Directory.GetFiles(thirdSettings.DownloadDirectory, "*.bin", SearchOption.AllDirectories).Single();
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(thirdPath, timeout.Token)));
        }
        finally
        {
            if (c is not null) await c.DisposeAsync(); if (b is not null) await b.DisposeAsync(); if (a is not null) await a.DisposeAsync();
            Directory.Delete(root, true); // Only this test's freshly created GUID directory.
        }
    }
    [Fact]
    public async Task DifferentGroupCannotReadCatalog()
    {
        var root = Path.Combine(Path.GetTempPath(), "PeerFerry-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        await using (var a = Node(root, "a", Settings(root, "a")))
        await using (var b = Node(root, "b", Settings(root, "b")))
        {
            await a.StartAsync(); await b.StartAsync(); await b.RememberPeerAsync(a.Descriptor); await b.RefreshAsync();
            Assert.Empty(b.Catalog()); Assert.False(Assert.Single(b.PeerSnapshots()).Online);
        }
        Directory.Delete(root, true);
    }
    [Fact]
    public async Task FolderTransferAndAdapterRecovery()
    {
        var root = Path.Combine(Path.GetTempPath(), "PeerFerry-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var folder = Path.Combine(root, "Папка"); Directory.CreateDirectory(Path.Combine(folder, "Вложенная"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Вложенная", "Текст.txt"), "Проверка вложенной папки");
        await File.WriteAllBytesAsync(Path.Combine(folder, "Файл.bin"), RandomNumberGenerator.GetBytes(1024 * 1024));
        await File.WriteAllTextAsync(Path.Combine(folder, "Пустой.txt"), "");
        var sa = Settings(root, "a"); var sb = Settings(root, "b", sa.GroupKey);
        IReadOnlyList<NetworkBinding> selected = TestBindings();
        try
        {
            await using var a = new MeshNode(new StateStore(Path.Combine(root, "a")), sa, () => selected, false);
            await using var b = Node(root, "b", sb); await a.StartAsync(); await b.StartAsync();
            var hash = await a.Torrents.PublishAsync(folder);
            var sourceFiles = a.Torrents.Files(hash);
            Assert.Equal(3, sourceFiles.Count); Assert.All(sourceFiles, f => Assert.Equal(100, f.Progress));
            Assert.Contains(sourceFiles, f => f.Path.Replace('\\', '/').EndsWith("Вложенная/Текст.txt"));
            Assert.Equal(sourceFiles.Sum(f => f.Size), Assert.Single(a.Torrents.Snapshot()).CompletedBytes);
            // The first endpoint is reachable but has the wrong certificate; the next endpoint must work.
            await b.RememberPeerAsync(a.Descriptor with { Endpoints = b.Descriptor.Endpoints.Concat(a.Descriptor.Endpoints).ToList() });
            await b.RefreshAsync(); Assert.True(Assert.Single(b.PeerSnapshots()).Online);
            selected = []; await a.RebindAsync(); Assert.Empty(a.Descriptor.Endpoints);
            await b.RefreshAsync(); Assert.Empty(b.Catalog());
            selected = TestBindings(); await a.RebindAsync(); await b.RefreshAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await b.DownloadAsync(Assert.Single(b.Catalog()), timeout.Token);
            await UntilAsync(() => b.Torrents.Snapshot()[0].Progress >= 100, timeout.Token);
            var downloaded = Directory.GetFiles(sb.DownloadDirectory, "*", SearchOption.AllDirectories);
            Assert.Equal(3, downloaded.Length);
            Assert.All(b.Torrents.Files(hash), f => Assert.Equal(100, f.Progress));
            Assert.Equal("Проверка вложенной папки", await File.ReadAllTextAsync(downloaded.Single(p => p.EndsWith("Текст.txt"))));
            await a.Torrents.PauseAsync(hash); Assert.Empty(a.Torrents.Catalog(a.Id, "a"));
            await a.Torrents.ResumeAsync(hash); await a.Torrents.RemoveAsync(hash); Assert.True(Directory.Exists(folder));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task DownloadsFromTwoPeersWithRateLimit()
    {
        var root = Path.Combine(Path.GetTempPath(), "PeerFerry-tests-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(root);
        var path = Path.Combine(root, "Multi-source.bin"); var data = RandomNumberGenerator.GetBytes(16 * 1024 * 1024); await File.WriteAllBytesAsync(path, data);
        var sa = Settings(root, "a"); var sb = Settings(root, "b", sa.GroupKey); var sc = Settings(root, "c", sa.GroupKey); sc.DownloadLimitKiB = 2048;
        try
        {
            await using var a = Node(root, "a", sa); await using var b = Node(root, "b", sb); await using var c = Node(root, "c", sc);
            await a.StartAsync(); await b.StartAsync(); await c.StartAsync();
            var hash = await a.Torrents.PublishAsync(path); Assert.Equal(hash, await b.Torrents.PublishAsync(path));
            await c.RememberPeerAsync(a.Descriptor); await c.RememberPeerAsync(b.Descriptor);
            await c.RefreshAsync(); Assert.Equal(2, Assert.Single(c.Catalog()).Sources.Count);
            await c.Torrents.DownloadAsync(a.Torrents.Metadata(hash), hash);
            var started = System.Diagnostics.Stopwatch.StartNew();
            await c.RefreshAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
            await UntilAsync(() => c.Torrents.Snapshot()[0].Connections >= 2, timeout.Token);
            Assert.True(c.Torrents.Snapshot()[0].Progress < 100);
            await UntilAsync(() => c.Torrents.Snapshot()[0].Progress >= 100, timeout.Token);
            Assert.True(started.Elapsed >= TimeSpan.FromSeconds(6), "Aggregate rate limit was bypassed.");
            var downloaded = Directory.GetFiles(sc.DownloadDirectory, "*.bin", SearchOption.AllDirectories).Single();
            Assert.Equal(SHA256.HashData(data), SHA256.HashData(await File.ReadAllBytesAsync(downloaded)));
        }
        finally { Directory.Delete(root, true); }
    }
    private static AppSettings Settings(string root, string name, string? key = null) => new()
    {
        DisplayName = name, GroupKey = key ?? new AppSettings().GroupKey, Port = FreePort(), NetworkMode = "Manual",
        DownloadDirectory = Path.Combine(root, name + "-downloads"),
    };
    private static MeshNode Node(string root, string name, AppSettings settings) => new(new StateStore(Path.Combine(root, name)), settings, TestBindings, false);
    private static IReadOnlyList<NetworkBinding> TestBindings() => [new("test", "Loopback test", "", IPAddress.Loopback, 8, 0, false)];
    private static int FreePort() { using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); return ((IPEndPoint)listener.LocalEndpoint).Port; }
    private static async Task UntilAsync(Func<bool> predicate, CancellationToken token)
    { while (!predicate()) await Task.Delay(150, token); }
}
