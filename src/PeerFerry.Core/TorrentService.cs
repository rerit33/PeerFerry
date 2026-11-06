using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using MonoTorrent;
using MonoTorrent.BEncoding;
using MonoTorrent.Client;
using MonoTorrent.Connections;

namespace PeerFerry.Core;

public sealed class TorrentService : IAsyncDisposable
{
    private readonly StateStore store;
    private readonly AppSettings settings;
    private readonly ClientEngine engine;
    private readonly ConcurrentDictionary<string, TorrentManager> managers = new();
    private readonly SemaphoreSlim operations = new(1);
    public int LocalPort { get; }
    public event Action<string>? Notice;
    public TorrentService(StateStore store, AppSettings settings)
    {
        this.store = store; this.settings = settings;
        using (var reservation = new TcpListener(IPAddress.Loopback, 0)) { reservation.Start(); LocalPort = ((IPEndPoint)reservation.LocalEndpoint).Port; }
        var builder = new EngineSettingsBuilder
        {
            AllowLocalPeerDiscovery = false, AllowPortForwarding = false, AutoSaveLoadDhtCache = false,
            DhtEndPoint = null, AutoSaveLoadFastResume = true, AutoSaveLoadMagnetLinkMetadata = false,
            CacheDirectory = Path.Combine(store.Root, "cache", settings.GroupId),
            ListenEndPoints = new Dictionary<string, IPEndPoint> { ["ipv4"] = new(IPAddress.Loopback, LocalPort) },
            MaximumConnections = 100, MaximumHalfOpenConnections = 12,
            MaximumDownloadRate = 0, MaximumUploadRate = 0, // Aggregate limits are enforced by the TLS gateway from the first byte.
            AllowedEncryption = [EncryptionType.PlainText], // TLS is provided by the authenticated outer transport.
        };
        // Defence in depth: the engine can only dial the loopback proxies created by our gateway.
        var original = Factories.Default;
        var factories = original.WithPeerConnectionCreator("ipv4", uri =>
        {
            if (!IPAddress.TryParse(uri.Host, out var ip) || !IPAddress.IsLoopback(ip)) throw new InvalidOperationException("Внешние соединения движка запрещены.");
            return original.CreatePeerConnection(uri)!;
        }).WithPeerConnectionCreator("ipv6", _ => throw new InvalidOperationException("Используйте транспорт PeerFerry."));
        engine = new ClientEngine(builder.ToSettings(), factories);
    }
    private static TorrentSettings ManagerSettings() => new TorrentSettingsBuilder
    { AllowDht = false, AllowPeerExchange = false, CreateContainingDirectory = true, MaximumConnections = 40 }.ToSettings();

    public async Task RestoreAsync()
    {
        foreach (var record in settings.Shares.Where(s => s.GroupId == settings.GroupId).ToArray())
        {
            try
            {
                var metadata = await File.ReadAllBytesAsync(Path.Combine(store.MetadataDirectory, record.MetadataFile));
                var torrent = ValidateMetadata(metadata, settings.GroupId);
                if (Hash(torrent) != record.Hash) throw new InvalidDataException("Контрольная сумма метаданных не совпала.");
                EnsureNoReparsePoints(record.SaveDirectory);
                var manager = await engine.AddAsync(torrent, record.SaveDirectory, ManagerSettings());
                managers[record.Hash] = manager;
                if (!record.Paused && !SourceChanged(record)) await TryStartAsync(manager, record);
                else if (SourceChanged(record)) { record.Paused = true; Notice?.Invoke($"Исходный файл изменён: {record.Name}. Добавьте его заново."); }
            }
            catch (Exception e) { Notice?.Invoke($"Не удалось восстановить «{record.Name}»: {e.Message}"); }
        }
    }
    public async Task<string> PublishAsync(string path, CancellationToken token = default)
    {
        await operations.WaitAsync(token);
        try
        {
            var source = new SafeFileSource(path);
            if (!source.Files.Any() || source.Files.Sum(f => f.Length) == 0) throw new InvalidDataException("Выберите непустой файл или папку.");
            var stamps = source.Files.Select(f => new SourceStamp(f.Source, f.Length, File.GetLastWriteTimeUtc(f.Source).Ticks)).ToList();
            var creator = new TorrentCreator(TorrentType.V1Only) { CreatedBy = "PeerFerry", PieceLength = TorrentCreator.RecommendedPieceSize(source.Files) };
            creator.SetCustomSecure("peerferry-group", new BEncodedString(settings.GroupId));
            var bytes = (await creator.CreateAsync(source, token)).Encode();
            var torrent = ValidateMetadata(bytes, settings.GroupId);
            var hash = Hash(torrent);
            if (managers.ContainsKey(hash)) return hash;
            if (stamps.Any(StampChanged)) throw new IOException("Файл изменился во время подготовки. Повторите добавление.");
            var saveDirectory = Path.GetDirectoryName(Path.GetFullPath(path))!;
            var record = new ShareRecord { Hash = hash, GroupId = settings.GroupId, Name = torrent.Name,
                MetadataFile = hash + ".torrent", SaveDirectory = saveDirectory, IsPublication = true, Sources = stamps };
            await File.WriteAllBytesAsync(Path.Combine(store.MetadataDirectory, record.MetadataFile), bytes, token);
            var manager = await engine.AddAsync(torrent, saveDirectory, ManagerSettings());
            // The creator just hashed these exact unchanged files. Avoid reading a large
            // publication a second time solely to tell the engine it is already complete.
            await manager.LoadFastResumeAsync(new FastResume(torrent.InfoHashes,
                new BitField(torrent.PieceCount).SetAll(true), new BitField(torrent.PieceCount)));
        managers[hash] = manager; lock (settings) settings.Shares.Add(record);
            await store.SaveAsync(settings);
            await manager.StartAsync();
            return hash;
        }
        finally { operations.Release(); }
    }
    public async Task DownloadAsync(byte[] metadata, string expectedHash, CancellationToken token = default)
    {
        await operations.WaitAsync(token);
        try
        {
            var torrent = ValidateMetadata(metadata, settings.GroupId);
            var hash = Hash(torrent);
            if (!string.Equals(hash, expectedHash, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Метаданные не соответствуют выбранному файлу.");
            if (managers.ContainsKey(hash)) return;
            Directory.CreateDirectory(settings.DownloadDirectory);
            EnsureNoReparsePoints(settings.DownloadDirectory);
            var destination = Path.Combine(settings.DownloadDirectory, Path.GetFileNameWithoutExtension(torrent.Name) + "-" + hash[..8]);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("Папка назначения уже существует. Выберите другую папку загрузки.");
            Directory.CreateDirectory(destination);
            var record = new ShareRecord { Hash = hash, GroupId = settings.GroupId, Name = torrent.Name, MetadataFile = hash + ".torrent", SaveDirectory = destination };
            await File.WriteAllBytesAsync(Path.Combine(store.MetadataDirectory, record.MetadataFile), metadata, token);
            var manager = await engine.AddAsync(torrent, destination, ManagerSettings());
            managers[hash] = manager; lock (settings) settings.Shares.Add(record); await store.SaveAsync(settings); await TryStartAsync(manager, record);
        }
        finally { operations.Release(); }
    }
    public static string Hash(Torrent torrent) => torrent.InfoHashes.V1!.ToHex().ToUpperInvariant();
    public static Torrent ValidateMetadata(byte[] metadata, string groupId)
    {
        if (metadata.Length > 4 * 1024 * 1024) throw new InvalidDataException("Слишком много метаданных.");
        var dictionary = BEncodedValue.Decode<BEncodedDictionary>(metadata);
        foreach (var key in new[] { "announce", "announce-list", "url-list", "httpseeds", "nodes" })
            if (dictionary.TryGetValue(key, out var value) && value is not BEncodedList { Count: 0 }) throw new InvalidDataException("Внешние источники в метаданных запрещены.");
        if (dictionary["info"] is not BEncodedDictionary info || !info.TryGetValue("peerferry-group", out var group) || group is not BEncodedString groupString || groupString.Text != groupId)
            throw new InvalidDataException("Этот файл принадлежит другой группе.");
        var torrent = Torrent.Load(metadata);
        ValidateRelativePath(torrent.Name);
        if (torrent.Name.Contains('/') || torrent.Name.Contains('\\') || torrent.Files.Count > 20_000 || torrent.Size <= 0 || torrent.IsPrivate)
            throw new InvalidDataException("Неподдерживаемые метаданные.");
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in torrent.Files)
        {
            ValidateRelativePath(file.Path);
            if (!seen.Add(file.Path.Replace('\\', '/'))) throw new InvalidDataException("Повторяющиеся имена файлов.");
        }
        foreach (var path in seen)
        {
            var parent = path;
            while (parent.LastIndexOf('/') is var slash && slash >= 0) { parent = parent[..slash]; if (seen.Contains(parent)) throw new InvalidDataException("Конфликт путей файлов."); }
        }
        return torrent;
    }
    public static void ValidateRelativePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || Path.IsPathRooted(path) || path.Length > 1024) throw new InvalidDataException("Недопустимый путь файла.");
        foreach (var part in path.Replace('\\', '/').Split('/'))
        {
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ') || part.Any(c => c < 32 || "<>:\"|?*".Contains(c)) ||
                stem is "CON" or "PRN" or "AUX" or "NUL" || (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && stem[3] is >= '0' and <= '9'))
                throw new InvalidDataException("Недопустимое имя файла.");
        }
    }
    public static void EnsureNoReparsePoints(string path)
    {
        for (var current = Path.GetFullPath(path); !string.IsNullOrEmpty(current); current = Path.GetDirectoryName(current))
            if ((Directory.Exists(current) || File.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("Папка загрузки содержит символическую ссылку. Выберите обычную папку.");
    }
    private static bool StampChanged(SourceStamp stamp) => !File.Exists(stamp.Path) || new FileInfo(stamp.Path).Length != stamp.Length || File.GetLastWriteTimeUtc(stamp.Path).Ticks != stamp.ModifiedTicks;
    private static bool SourceChanged(ShareRecord record) => record.IsPublication && record.Sources.Any(StampChanged);
    public async Task CheckSourcesAsync()
    {
        ShareRecord[] records; lock (settings) records = settings.Shares.Where(s => s.GroupId == settings.GroupId && s.IsPublication && !s.Paused).ToArray();
        foreach (var record in records)
            if (SourceChanged(record)) { await PauseAsync(record.Hash); Notice?.Invoke($"Исходный файл изменён: {record.Name}. Раздача приостановлена."); }
    }
    public List<CatalogEntry> Catalog(string id, string name) => managers.Values.Where(m => m.State is not (TorrentState.Stopped or TorrentState.Paused or TorrentState.Error))
        .Select(m => new CatalogEntry(Hash(m.Torrent!), m.Name, m.Torrent!.Size, m.Files.Count, id, name)).Take(1000).ToList();
    public byte[] Metadata(string hash)
    {
        if (!managers.TryGetValue(hash, out var manager) || manager.State is TorrentState.Stopped or TorrentState.Paused or TorrentState.Error)
            throw new FileNotFoundException("Раздача недоступна.");
        return File.ReadAllBytes(Path.Combine(store.MetadataDirectory, hash + ".torrent"));
    }
    public IReadOnlyList<TransferSnapshot> Snapshot()
    {
        Dictionary<string, ShareRecord> records;
        lock (settings) records = settings.Shares.Where(s => s.GroupId == settings.GroupId).ToDictionary(s => s.Hash);
        return managers.Select(p =>
    {
        var m = p.Value; if (!records.TryGetValue(p.Key, out var record)) return null;
        return new TransferSnapshot(p.Key, m.Name, m.Torrent!.Size, m.Progress, m.Monitor.DownloadRate, m.Monitor.UploadRate,
            m.OpenConnections, m.State == TorrentState.Stopped && !record.Paused ? "Queued" : m.State.ToString(), m.ContainingDirectory, record.IsPublication);
    }).OfType<TransferSnapshot>().OrderBy(s => s.Name).ToArray();
    }
    private async Task TryStartAsync(TorrentManager manager, ShareRecord record)
    {
        if (manager.State is not (TorrentState.Stopped or TorrentState.Paused)) return;
        var active = managers.Values.Count(m => m.Progress < 100 && m.State is not (TorrentState.Stopped or TorrentState.Paused or TorrentState.Error));
        if (record.IsPublication || manager.Progress >= 100 || active < settings.ConcurrentDownloads) await manager.StartAsync();
    }
    public async Task MaintainQueueAsync()
    {
        await operations.WaitAsync();
        try
        {
            ShareRecord[] records; lock (settings) records = settings.Shares.Where(s => s.GroupId == settings.GroupId && !s.Paused).ToArray();
            foreach (var record in records)
                if (managers.TryGetValue(record.Hash, out var manager) && manager.State == TorrentState.Stopped) await TryStartAsync(manager, record);
        }
        finally { operations.Release(); }
    }
    public async Task AddPeerAsync(int proxyPort)
    {
        foreach (var manager in managers.Values.Where(m => m.State is TorrentState.Downloading or TorrentState.Seeding))
            try { await manager.AddPeerAsync(new PeerInfo(new Uri($"ipv4://127.0.0.1:{proxyPort}"))); } catch (InvalidOperationException) { }
    }
    public async Task PauseAsync(string hash)
    {
        await operations.WaitAsync();
        try
        {
            if (!managers.TryGetValue(hash, out var manager)) return;
            lock (settings) { var record = settings.Shares.FirstOrDefault(s => s.Hash == hash && s.GroupId == settings.GroupId); if (record is not null) record.Paused = true; }
            await StopManagerAsync(manager); await store.SaveAsync(settings);
        }
        finally { operations.Release(); }
    }
    public async Task ResumeAsync(string hash)
    {
        await operations.WaitAsync();
        try
        {
            if (!managers.TryGetValue(hash, out var manager)) return;
            ShareRecord record; lock (settings) record = settings.Shares.First(s => s.Hash == hash && s.GroupId == settings.GroupId);
            if (SourceChanged(record)) throw new IOException("Исходный файл изменён. Удалите раздачу из списка и добавьте файл заново.");
            record.Paused = false; await store.SaveAsync(settings); await TryStartAsync(manager, record);
        }
        finally { operations.Release(); }
    }
    public async Task RemoveAsync(string hash)
    {
        await operations.WaitAsync();
        try
        {
            if (!managers.TryRemove(hash, out var manager)) return;
            await StopManagerAsync(manager); await engine.RemoveAsync(manager, RemoveMode.CacheDataOnly);
            lock (settings) settings.Shares.RemoveAll(s => s.Hash == hash && s.GroupId == settings.GroupId); await store.SaveAsync(settings);
        }
        finally { operations.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        foreach (var m in managers.Values) { try { await StopManagerAsync(m); } catch (Exception e) { Notice?.Invoke(e.Message); } }
        engine.Dispose();
    }
    private static async Task StopManagerAsync(TorrentManager manager)
    {
        // MonoTorrent 3.0.2 starts asynchronously. Let StartingMode finish before replacing
        // it with StoppingMode, otherwise its continuation can race a fast stop/restart.
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (manager.State == TorrentState.Starting && DateTime.UtcNow < deadline) await Task.Delay(20);
        await manager.StopAsync();
    }
    private sealed class SafeFileSource : ITorrentFileSource
    {
        public IEnumerable<FileMapping> Files { get; }
        public string TorrentName { get; }
        public SafeFileSource(string path)
        {
            path = Path.GetFullPath(path); TorrentName = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar)); ValidateRelativePath(TorrentName);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Выберите файл или папку без символической ссылки.");
            var files = File.Exists(path) ? [path] : Directory.EnumerateFiles(path, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System }).ToArray();
            if (files.Length > 20_000) throw new IOException("В первой версии поддерживается до 20 000 файлов в одной раздаче.");
            Files = files.Select(f => new FileMapping(f, File.Exists(path) ? TorrentName : Path.GetRelativePath(path, f), new FileInfo(f).Length)).ToArray();
        }
    }
}
