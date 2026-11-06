using System.ComponentModel;
using PeerFerry.Core;

namespace PeerFerry.App;

internal static class Display
{
    public static string Size(long bytes)
    {
        string[] units = ["Б", "КиБ", "МиБ", "ГиБ", "ТиБ"]; double value = bytes; var i = 0;
        while (value >= 1024 && i < units.Length - 1) { value /= 1024; i++; }
        return $"{value:0.#} {units[i]}";
    }
    public static string Rate(long value) => value > 0 ? Size(value) + "/с" : "—";
    public static string Percent(double? value) => value is null ? "—" : $"{value:0.#}%";
    public static string Icon(string name, int count = 1) => count > 1 ? "Folder" : Path.GetExtension(name).ToLowerInvariant() switch { ".zip" or ".7z" or ".rar" => "Archive", ".mp4" or ".mkv" or ".avi" => "Video", "" => "Folder", _ => "File" };
    public static string State(string state) => state switch
    { "Stopped" or "Paused" => "Приостановлено", "Queued" => "В очереди", "Hashing" => "Проверка файлов", "HashingPaused" => "Проверка на паузе", "Downloading" => "Загрузка", "Seeding" => "Готово · раздаётся", "Starting" => "Запуск", "Stopping" => "Остановка", "Error" => "Ошибка", _ => state };
}
internal abstract class LiveRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;
    protected void Changed() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
internal sealed class TransferRow(TransferSnapshot snapshot) : LiveRow
{
    public TransferSnapshot Snapshot { get; private set; } = snapshot;
    public void Update(TransferSnapshot value) { if (Snapshot != value) { Snapshot = value; Changed(); } }
    public string Hash => Snapshot.Hash;
    public string Name => Snapshot.Name;
    public string Icon => Display.Icon(Name, Snapshot.FileCount);
    public string SizeText => Display.Size(Snapshot.Size);
    public double Progress => Snapshot.Progress;
    public string ProgressText => Progress >= 100 ? $"{SizeText} (100%)" : $"{Display.Size(Snapshot.CompletedBytes)} из {SizeText} ({Progress:0.#}%)";
    public string StateText => Display.State(Snapshot.State);
    public string DownloadText => Display.Rate(Snapshot.DownloadSpeed);
    public string UploadText => Display.Rate(Snapshot.UploadSpeed);
    public bool CanResume => Snapshot.State is "Stopped" or "Paused" or "HashingPaused" or "Queued" or "Error";
    public string ActionIcon => CanResume ? "Play" : "Pause";
    public string ActionHint => CanResume ? "Продолжить передачу" : "Приостановить передачу";
    public string RemainingText
    {
        get
        {
            if (Progress >= 100) return "Готово";
            if (Snapshot.DownloadSpeed <= 0) return "—";
            var seconds = Math.Max(1, (Snapshot.Size - Snapshot.CompletedBytes) / Snapshot.DownloadSpeed);
            return seconds >= 3600 ? $"≈ {seconds / 3600} ч {seconds % 3600 / 60} мин" : seconds >= 60 ? $"≈ {seconds / 60} мин" : $"≈ {seconds} с";
        }
    }
}
internal sealed class CatalogRow(RemoteShare share, TransferSnapshot? local) : LiveRow
{
    public RemoteShare Share { get; private set; } = share;
    public TransferSnapshot? Local { get; private set; } = local;
    public void Update(RemoteShare value, TransferSnapshot? transfer) { Share = value; Local = transfer; Changed(); }
    public string Hash => Share.Entry.Hash;
    public string Name => Share.Entry.Name;
    public string Icon => Display.Icon(Name, Share.Entry.FileCount);
    public string SizeText => Display.Size(Share.Entry.Size);
    public string FileCountText => Share.Entry.FileCount == 1 ? "1 файл" : $"Файлов: {Share.Entry.FileCount}";
    public string Owners => string.Join(", ", Share.Sources.Select(s => s.Name).Distinct().Order());
    public string Status => Local is null ? "Можно скачать" : Local.Progress >= 100 ? "Уже скачан" : "Уже скачивается";
    public string Action => Local is null ? "Скачать" : Local.Progress >= 100 ? "Открыть" : "К передаче";
}
internal sealed record FileRow(ShareFileSnapshot File)
{
    public string Path => File.Path;
    public string Icon => Display.Icon(Path);
    public string Size => Display.Size(File.Size);
    public string Progress => File.Progress is null ? "Не загружен" : Display.Percent(File.Progress);
}
internal sealed record ParticipantRow(ShareParticipantSnapshot Peer)
{
    public string Name => Peer.Name;
    public string Download => Display.Rate(Peer.DownloadSpeed);
    public string Upload => Display.Rate(Peer.UploadSpeed);
    public string Available => Display.Percent(Peer.Progress);
    public double Progress => Peer.Progress ?? 0;
    public string Status => Peer.Connected ? "Подключён" : Peer.Online ? "В сети" : "Нет связи";
}
