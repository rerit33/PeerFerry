using System.Collections.Concurrent;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace PeerFerry.Core;

public sealed class MeshNode : IAsyncDisposable
{
    private sealed class PeerState(PeerDescriptor peer)
    {
        public PeerDescriptor Peer = peer;
        public DateTimeOffset LastSeen;
        public string Error = "Ожидание подключения";
        public List<CatalogEntry> Catalog = [];
        public TcpListener? Proxy;
    }
    private readonly StateStore store;
    private readonly Identity identity;
    private readonly Func<IReadOnlyList<NetworkBinding>> bindingProvider;
    private readonly ConcurrentDictionary<string, PeerState> peers = new();
    private readonly ConcurrentDictionary<int, Task> connections = new();
    private readonly ConcurrentDictionary<int, string> incomingTorrentPeers = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim inboundSlots = new(48);
    private readonly SemaphoreSlim rebindLock = new(1);
    private readonly SemaphoreSlim refreshLock = new(1);
    private readonly bool enableDiscovery;
    private readonly BandwidthLimiter upload;
    private readonly BandwidthLimiter download;
    private readonly List<TcpListener> listeners = [];
    private CancellationTokenSource network = new();
    private Task? maintenance;
    private DiscoveryService? discovery;
    private int connectionId;
    private NetworkBinding[] bindings = [];
    private string requestedBindingSignature = "";
    public AppSettings Settings { get; }
    public TorrentService Torrents { get; }
    public string Id => identity.Id;
    public IReadOnlyList<NetworkBinding> Bindings => bindings;
    public event Action<string>? Notice;
    public event Action<string>? Diagnostic;
    public PeerDescriptor Descriptor => new(Id, Settings.DisplayName, bindings.Select(b => new PeerEndpoint(b.Address.ToString(), Settings.Port)).Distinct().ToList());
    public MeshNode(StateStore store, AppSettings settings, Func<IReadOnlyList<NetworkBinding>>? bindingProvider = null, bool enableDiscovery = true)
    {
        this.store = store; Settings = settings; identity = new Identity(store.Root); this.enableDiscovery = enableDiscovery;
        upload = new(settings.UploadLimitKiB); download = new(settings.DownloadLimitKiB);
        this.bindingProvider = bindingProvider ?? (() => NetworkAdapters.Select(settings, NetworkAdapters.Enumerate()));
        Torrents = new TorrentService(store, settings);
        Torrents.Notice += message => Notice?.Invoke(message);
        foreach (var peer in settings.KnownPeers.Take(128))
        { try { Invitations.ValidatePeer(peer); if (peer.Id != Id) peers.TryAdd(peer.Id, new(peer)); } catch (InvalidDataException) { } }
    }
    public async Task StartAsync()
    {
        await store.SaveAsync(Settings);
        await Torrents.RestoreAsync();
        await RebindAsync();
        maintenance = Task.Run(MaintainAsync);
    }
    public string CreateInvitation()
    {
        if (bindings.Length == 0) throw new InvalidOperationException("Выберите хотя бы один работающий сетевой адаптер.");
        return Invitations.Encode(new(1, Settings.GroupName, Settings.GroupKey, Descriptor));
    }
    public async Task RememberPeerAsync(PeerDescriptor peer)
    {
        Invitations.ValidatePeer(peer);
        if (peer.Id == Id || peers.Count >= 128 && !peers.ContainsKey(peer.Id)) return;
        var state = peers.GetOrAdd(peer.Id, _ => new(peer));
        bool changed;
        lock (Settings) changed = !Settings.KnownPeers.Any(p => p.Id == peer.Id && p.Name == peer.Name && p.Endpoints.SequenceEqual(peer.Endpoints));
        if (!changed) return;
        state.Peer = peer;
        lock (Settings) Settings.KnownPeers = peers.Values.Select(s => s.Peer).ToList();
        await store.SaveAsync(Settings);
    }
    public async Task RebindAsync()
    {
        await rebindLock.WaitAsync(lifetime.Token);
        await refreshLock.WaitAsync(lifetime.Token);
        try
        {
            await network.CancelAsync(); network.Dispose();
            foreach (var listener in listeners) listener.Stop(); listeners.Clear();
            if (discovery is not null) { await discovery.DisposeAsync(); discovery = null; }
            foreach (var peer in peers.Values) { peer.Proxy?.Stop(); peer.Proxy = null; peer.LastSeen = default; peer.Catalog = []; }
            network = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
            var requested = bindingProvider().DistinctBy(b => b.Address).ToArray();
            requestedBindingSignature = Signature(requested);
            var active = new List<NetworkBinding>();
            foreach (var binding in requested)
            {
                var listener = new TcpListener(binding.Address, Settings.Port);
                try { listener.Server.ExclusiveAddressUse = true; listener.Start(32); listeners.Add(listener); active.Add(binding); Track(AcceptLoopAsync(listener, network.Token)); }
                catch (SocketException e) { listener.Stop(); Notice?.Invoke($"Не удалось открыть {binding.Name}: {e.Message}"); }
            }
            bindings = active.ToArray();
            if (bindings.Length > 0)
            {
                if (enableDiscovery)
                {
                    discovery = new DiscoveryService(Settings.GroupKey, () => Descriptor, bindings, peer => Track(RememberPeerAsync(peer)), message => Notice?.Invoke(message));
                    discovery.Start();
                }
                Notice?.Invoke($"Сеть включена: {string.Join(", ", bindings.Select(b => b.Name).Distinct())}");
            }
            else Notice?.Invoke("Нет выбранных доступных IPv4-адаптеров. Откройте настройки сети.");
        }
        finally { refreshLock.Release(); rebindLock.Release(); }
    }
    private static string Signature(IEnumerable<NetworkBinding> items) => string.Join("|", items.Select(b => $"{b.AdapterId}:{b.Address}:{b.Index}").Order());
    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref connectionId); connections[id] = task;
        _ = task.ContinueWith(done => { connections.TryRemove(id, out _); if (done.Exception is not null) Notice?.Invoke(done.Exception.GetBaseException().Message); }, TaskScheduler.Default);
    }
    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(token);
                if (!inboundSlots.Wait(0)) { client.Dispose(); continue; }
                Track(ServeAsync(client, token));
            }
        }
        catch (Exception e) when (e is OperationCanceledException or SocketException or ObjectDisposedException) { }
    }
    private static SslServerAuthenticationOptions ServerOptions(Identity identity) => new()
    {
        ServerCertificate = identity.Certificate, ClientCertificateRequired = true,
        EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
    };
    private static string ProofText(string side, string nonce, string client, string server) => $"PeerFerry/1/{side}/{nonce}/{client}/{server}";
    private async Task ServeAsync(TcpClient client, CancellationToken token)
    {
        using (client)
        using (var ssl = new SslStream(client.GetStream(), false, (_, certificate, _, _) => certificate is not null))
        {
            try
            {
                ConfigureSocket(client);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await ssl.AuthenticateAsServerAsync(ServerOptions(identity), deadline.Token);
                var remoteId = Identity.Fingerprint(ssl.RemoteCertificate!);
                var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
                await Wire.WriteAsync(ssl, new Challenge(nonce, Id), deadline.Token);
                var hello = await Wire.ReadAsync<Hello>(ssl, deadline.Token, 16_384);
                Invitations.ValidatePeer(hello.Peer);
                if (hello.GroupId != Settings.GroupId || hello.Peer.Id != remoteId ||
                    !Crypto.Verify(Settings.GroupKey, ProofText("client", nonce, remoteId, Id), hello.Proof)) throw new AuthenticationException("Нет доступа к группе.");
                await Wire.WriteAsync(ssl, new Welcome(Crypto.Mac(Settings.GroupKey, ProofText("server", nonce, remoteId, Id)), Descriptor), deadline.Token);
                await RememberPeerAsync(hello.Peer);
                var request = await Wire.ReadAsync<Request>(ssl, deadline.Token, 1024);
                switch (request.Command)
                {
                    case "catalog":
                        await Wire.WriteAsync(ssl, new CatalogResponse(Descriptor, peers.Values.Select(p => p.Peer).Take(128).ToList(), Torrents.Catalog(Id, Settings.DisplayName)), deadline.Token);
                        break;
                    case "metadata":
                        if (request.Hash is null || request.Hash.Length != 40 || !request.Hash.All(Uri.IsHexDigit)) throw new InvalidDataException("Некорректный идентификатор раздачи.");
                        await Wire.WriteAsync(ssl, Convert.ToBase64String(Torrents.Metadata(request.Hash)), deadline.Token);
                        break;
                    case "torrent":
                        using (var local = new TcpClient())
                        {
                            await local.ConnectAsync(IPAddress.Loopback, Torrents.LocalPort, deadline.Token);
                            var localPort = ((IPEndPoint)local.Client.LocalEndPoint!).Port;
                            incomingTorrentPeers[localPort] = remoteId;
                            try
                            {
                                await Wire.WriteAsync(ssl, new Response(true), deadline.Token);
                                deadline.CancelAfter(Timeout.InfiniteTimeSpan);
                                await Wire.BridgeAsync(local.GetStream(), ssl, token, upload, download);
                            }
                            finally { incomingTorrentPeers.TryRemove(localPort, out _); }
                        }
                        break;
                    default: throw new InvalidDataException("Неизвестная команда.");
                }
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException or NullReferenceException) { Diagnostic?.Invoke($"Входящее соединение: {e.GetBaseException().Message}"); }
            finally { inboundSlots.Release(); }
        }
    }
    private sealed class SecureConnection(TcpClient client, SslStream stream) : IDisposable
    {
        public SslStream Stream => stream;
        public void Dispose() { stream.Dispose(); client.Dispose(); }
    }
    private static void ConfigureSocket(TcpClient client)
    {
        client.NoDelay = true;
        client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.KeepAlive, true);
        if (OperatingSystem.IsWindows())
        {
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveTime, 20);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveInterval, 5);
            client.Client.SetSocketOption(SocketOptionLevel.Tcp, SocketOptionName.TcpKeepAliveRetryCount, 3);
        }
    }
    private async Task<SecureConnection> ConnectAsync(PeerDescriptor peer, CancellationToken token)
    {
        Exception? failure = null;
        // Prefer the matching subnet, then ZeroTier, and try every advertised allowed endpoint.
        var attempts = peer.Endpoints.SelectMany(ep => bindings.Select(b => (Endpoint: ep, Binding: b)))
            .OrderByDescending(a => a.Binding.Contains(IPAddress.Parse(a.Endpoint.Address)))
            .ThenByDescending(a => a.Binding.Name.Contains("zerotier", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var attempt in attempts)
        {
            token.ThrowIfCancellationRequested();
            var client = new TcpClient(AddressFamily.InterNetwork); SslStream? ssl = null;
            try
            {
                NetworkAdapters.Bind(client.Client, attempt.Binding); ConfigureSocket(client);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(3));
                await client.ConnectAsync(IPAddress.Parse(attempt.Endpoint.Address), attempt.Endpoint.Port, deadline.Token);
                ssl = new SslStream(client.GetStream(), false, (_, cert, _, _) => cert is not null && Identity.Fingerprint(cert) == peer.Id);
                await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "PeerFerry", ClientCertificates = new X509CertificateCollection { identity.Certificate },
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13, CertificateRevocationCheckMode = X509RevocationMode.NoCheck,
                }, deadline.Token);
                var challenge = await Wire.ReadAsync<Challenge>(ssl, deadline.Token, 1024);
                if (challenge.Id != peer.Id || challenge.Nonce.Length > 100) throw new AuthenticationException("Идентификатор участника не совпал.");
                await Wire.WriteAsync(ssl, new Hello(Descriptor, Settings.GroupId, Crypto.Mac(Settings.GroupKey, ProofText("client", challenge.Nonce, Id, peer.Id))), deadline.Token);
                var welcome = await Wire.ReadAsync<Welcome>(ssl, deadline.Token, 16_384);
                if (!Crypto.Verify(Settings.GroupKey, ProofText("server", challenge.Nonce, Id, peer.Id), welcome.Proof)) throw new AuthenticationException("Неверный ключ группы.");
                Invitations.ValidatePeer(welcome.Peer);
                if (welcome.Peer.Id != peer.Id) throw new AuthenticationException("Идентификатор участника изменился.");
                return new SecureConnection(client, ssl);
            }
            catch (Exception e) when (e is SocketException or IOException or AuthenticationException or OperationCanceledException or InvalidDataException)
            { failure = e; ssl?.Dispose(); client.Dispose(); }
        }
        throw new IOException("Не удалось подключиться через выбранные адаптеры. Проверьте VPN и брандмауэр.", failure);
    }
    public async Task RefreshAsync(CancellationToken token = default)
    {
        await refreshLock.WaitAsync(token);
        try
        {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, network.Token);
        using var slots = new SemaphoreSlim(4);
        await Task.WhenAll(peers.Values.ToArray().Select(async state =>
        {
            await slots.WaitAsync(linked.Token);
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(linked.Token); deadline.CancelAfter(TimeSpan.FromSeconds(12));
                using var connection = await ConnectAsync(state.Peer, deadline.Token);
                await Wire.WriteAsync(connection.Stream, new Request("catalog"), deadline.Token);
                var catalog = await Wire.ReadAsync<CatalogResponse>(connection.Stream, deadline.Token);
                Invitations.ValidatePeer(catalog.Peer);
                if (catalog.Peer.Id != state.Peer.Id || catalog.Files.Count > 1000 || catalog.Peers.Count > 128) throw new InvalidDataException("Некорректный каталог.");
                state.Peer = catalog.Peer; state.Catalog = catalog.Files.Where(f => f is not null && f.Hash is not null && f.Hash.Length == 40 && f.Hash.All(Uri.IsHexDigit) && f.OwnerId == state.Peer.Id && !string.IsNullOrEmpty(f.Name) && f.Name.Length <= 1024 && f.Size > 0 && f.FileCount is > 0 and <= 20_000).ToList();
                state.LastSeen = DateTimeOffset.UtcNow; state.Error = "";
                foreach (var known in catalog.Peers) await RememberPeerAsync(known);
                if (state.Proxy is null)
                {
                    var proxy = new TcpListener(IPAddress.Loopback, 0); proxy.Start(); state.Proxy = proxy;
                    Track(ProxyLoopAsync(proxy, state, network.Token));
                }
                await Torrents.AddPeerAsync(((IPEndPoint)state.Proxy.LocalEndpoint).Port);
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException or InvalidDataException or System.Text.Json.JsonException)
            { state.Error = $"Нет связи: {e.GetBaseException().Message}"; state.Catalog = []; }
            finally { slots.Release(); }
        }));
        }
        finally { refreshLock.Release(); }
    }
    private async Task ProxyLoopAsync(TcpListener proxy, PeerState state, CancellationToken token)
    {
        try { while (!token.IsCancellationRequested) { var client = await proxy.AcceptTcpClientAsync(token); Track(ProxyAsync(client, state, token)); } }
        catch (Exception e) when (e is SocketException or OperationCanceledException or ObjectDisposedException) { }
    }
    private async Task ProxyAsync(TcpClient client, PeerState peer, CancellationToken token)
    {
        using (client)
        {
            try
            {
                using var connection = await ConnectAsync(peer.Peer, token);
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token); deadline.CancelAfter(TimeSpan.FromSeconds(10));
                await Wire.WriteAsync(connection.Stream, new Request("torrent"), deadline.Token);
                if (!(await Wire.ReadAsync<Response>(connection.Stream, deadline.Token)).Ok) return;
                await Wire.BridgeAsync(client.GetStream(), connection.Stream, token, upload, download);
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or OperationCanceledException) { }
        }
    }
    public IReadOnlyList<RemoteShare> Catalog() => peers.Values.Where(p => p.Error == "" && DateTimeOffset.UtcNow - p.LastSeen < TimeSpan.FromSeconds(30))
        .SelectMany(p => p.Catalog.Select(f => (File: f, Source: p.Peer))).GroupBy(p => p.File.Hash)
        .Select(g => new RemoteShare(g.First().File, g.Select(p => p.Source).ToList())).OrderBy(f => f.Entry.Name).ToArray();
    public IReadOnlyList<PeerSnapshot> PeerSnapshots() => peers.Values.Select(p => new PeerSnapshot(p.Peer.Id, p.Peer.Name,
        string.Join(", ", p.Peer.Endpoints.Select(e => e.Address).Distinct()), p.Error == "" && DateTimeOffset.UtcNow - p.LastSeen < TimeSpan.FromSeconds(30), p.LastSeen, p.Error)).OrderBy(p => p.Name).ToArray();
    public IReadOnlyList<ShareParticipantSnapshot> CatalogParticipants(string hash)
    {
        var result = new List<ShareParticipantSnapshot>();
        foreach (var peer in peers.Values)
        {
            // Refresh/rebind can replace the catalog while the UI reads it.
            var entry = peer.Catalog.FirstOrDefault(file => file.Hash == hash);
            if (entry is not null) result.Add(new(peer.Peer.Id, peer.Peer.Name, 0, 0,
                entry.Progress is >= 0 and <= 100 ? entry.Progress : null,
                peer.Error == "" && DateTimeOffset.UtcNow - peer.LastSeen < TimeSpan.FromSeconds(30), false));
        }
        return result.OrderBy(p => p.Name).ToArray();
    }
    public async Task<IReadOnlyList<ShareParticipantSnapshot>> TransferParticipantsAsync(string hash)
    {
        var lookup = new Dictionary<int, string>();
        foreach (var peer in peers.Values)
        {
            try { if (peer.Proxy?.LocalEndpoint is IPEndPoint endpoint) lookup[endpoint.Port] = peer.Peer.Id; }
            catch (InvalidOperationException) { /* The selected adapter may have disappeared. */ }
        }
        foreach (var entry in incomingTorrentPeers) lookup[entry.Key] = entry.Value;
        var result = CatalogParticipants(hash).ToDictionary(p => p.Id);
        foreach (var group in (await Torrents.ConnectionsAsync(hash)).Where(c => lookup.ContainsKey(c.LocalPort)).GroupBy(c => lookup[c.LocalPort]))
            if (peers.TryGetValue(group.Key, out var peer)) result[group.Key] = new(group.Key, peer.Peer.Name,
                group.Sum(c => c.DownloadSpeed), group.Sum(c => c.UploadSpeed), group.Max(c => c.Progress), true, true);
        return result.Values.OrderBy(p => p.Name).ToArray();
    }
    public async Task<byte[]> GetMetadataAsync(RemoteShare share, CancellationToken token = default)
    {
        Exception? lastFailure = null;
        foreach (var source in share.Sources)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, network.Token); deadline.CancelAfter(TimeSpan.FromSeconds(15));
                using var connection = await ConnectAsync(source, deadline.Token);
                await Wire.WriteAsync(connection.Stream, new Request("metadata", share.Entry.Hash), deadline.Token);
                var encoded = await Wire.ReadAsync<string>(connection.Stream, deadline.Token);
                var bytes = Convert.FromBase64String(encoded);
                var torrent = TorrentService.ValidateMetadata(bytes, Settings.GroupId);
                if (TorrentService.Hash(torrent) != share.Entry.Hash || torrent.Size != share.Entry.Size || torrent.Files.Count != share.Entry.FileCount)
                    throw new InvalidDataException("Метаданные не соответствуют каталогу.");
                return bytes;
            }
            catch (Exception e) when (e is IOException or SocketException or AuthenticationException or FormatException or OperationCanceledException)
            { if (token.IsCancellationRequested) throw; lastFailure = e; }
        }
        throw new IOException("Не удалось получить содержимое раздачи. Проверьте подключение участников.", lastFailure);
    }
    public async Task DownloadAsync(RemoteShare share, CancellationToken token = default, byte[]? metadata = null)
    {
        await Torrents.DownloadAsync(metadata ?? await GetMetadataAsync(share, token), share.Entry.Hash, token);
        await RefreshAsync(token);
    }
    private async Task MaintainAsync()
    {
        var tick = 0;
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                try
                {
                    if (Signature(bindingProvider()) != requestedBindingSignature || bindings.Length == 0 && bindingProvider().Count > 0) await RebindAsync();
                    if (bindings.Length > 0) await RefreshAsync(lifetime.Token);
                    await Torrents.MaintainQueueAsync();
                    if (++tick % 3 == 0) await Torrents.CheckSourcesAsync();
                }
                catch (Exception e) when (e is not OutOfMemoryException) { if (!lifetime.IsCancellationRequested) Notice?.Invoke(e.Message); }
                await Task.Delay(TimeSpan.FromSeconds(4), lifetime.Token);
            }
        }
        catch (OperationCanceledException) { }
    }
    public async ValueTask DisposeAsync()
    {
        await lifetime.CancelAsync(); await network.CancelAsync();
        foreach (var listener in listeners) listener.Stop();
        foreach (var peer in peers.Values) peer.Proxy?.Stop();
        if (discovery is not null) await discovery.DisposeAsync();
        if (maintenance is not null) await maintenance;
        try { await Task.WhenAll(connections.Values.ToArray()).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is TimeoutException or OperationCanceledException or IOException) { }
        await Torrents.DisposeAsync(); await store.SaveAsync(Settings); identity.Dispose(); network.Dispose(); lifetime.Dispose();
    }
}
