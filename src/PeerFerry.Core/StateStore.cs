using System.Text.Json;

namespace PeerFerry.Core;

public sealed class StateStore
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private readonly SemaphoreSlim writeLock = new(1);
    public string Root { get; }
    public string MetadataDirectory => Path.Combine(Root, "metadata");
    public StateStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(MetadataDirectory);
    }
    public AppSettings Load()
    {
        var path = Path.Combine(Root, "settings.json");
        if (!File.Exists(path)) return new();
        // A damaged configuration must never silently generate a different group.
        var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Не удалось прочитать настройки PeerFerry.");
        Validate(settings);
        return settings;
    }
    public static void Validate(AppSettings settings)
    {
        if (settings.SchemaVersion != 1) throw new InvalidDataException("Эта версия настроек не поддерживается.");
        if (Convert.FromBase64String(settings.GroupKey).Length != 32) throw new InvalidDataException("Некорректный ключ группы.");
        if (settings.Port is < 1024 or > 65535) throw new InvalidDataException("Порт должен быть от 1024 до 65535.");
        if (settings.NetworkMode is not ("Auto" or "Manual" or "All")) throw new InvalidDataException("Некорректный режим сети.");
        if (settings.DownloadLimitKiB is < 0 or > 1_000_000 || settings.UploadLimitKiB is < 0 or > 1_000_000)
            throw new InvalidDataException("Некорректный лимит скорости.");
        if (settings.ConcurrentDownloads is < 1 or > 20) throw new InvalidDataException("Число загрузок должно быть от 1 до 20.");
        if (string.IsNullOrWhiteSpace(settings.DisplayName) || settings.DisplayName.Length > 80)
            throw new InvalidDataException("Имя должно содержать от 1 до 80 символов.");
        _ = Path.GetFullPath(settings.DownloadDirectory);
    }
    public async Task SaveAsync(AppSettings settings)
    {
        await writeLock.WaitAsync();
        try
        {
            byte[] bytes;
            lock (settings) bytes = JsonSerializer.SerializeToUtf8Bytes(settings, Json);
            var path = Path.Combine(Root, "settings.json");
            await File.WriteAllBytesAsync(path + ".tmp", bytes);
            File.Move(path + ".tmp", path, true);
        }
        finally { writeLock.Release(); }
    }
}
