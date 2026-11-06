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
    public static string State(string state) => state switch
    { "Stopped" => "Приостановлено", "Paused" => "Приостановлено", "Queued" => "В очереди", "Hashing" => "Проверка файлов", "HashingPaused" => "Проверка на паузе", "Downloading" => "Загрузка", "Seeding" => "Раздача", "Starting" => "Запуск", "Stopping" => "Остановка", "Error" => "Ошибка", _ => state };
}
internal sealed record TransferRow(TransferSnapshot Snapshot)
{
    public string Hash => Snapshot.Hash;
    public string Name => Snapshot.Name;
    public string SizeText => Display.Size(Snapshot.Size);
    public double Progress => Snapshot.Progress;
    public string ProgressText => $"{Progress:0.#}%";
    public string StateText => Display.State(Snapshot.State);
    public string DownloadText => Display.Size(Snapshot.DownloadSpeed) + "/с";
    public string UploadText => Display.Size(Snapshot.UploadSpeed) + "/с";
    public int Connections => Snapshot.Connections;
}
internal sealed record CatalogRow(RemoteShare Share)
{
    public string Hash => Share.Entry.Hash;
    public string Name => Share.Entry.Name;
    public string SizeText => Display.Size(Share.Entry.Size);
    public int FileCount => Share.Entry.FileCount;
    public string Owners => string.Join(", ", Share.Sources.Select(s => s.Name).Distinct());
}
internal sealed record PeerRow(PeerSnapshot Snapshot)
{
    public string Id => Snapshot.Id;
    public string Name => Snapshot.Name;
    public string Status => Snapshot.Online ? "В сети" : "Нет связи";
    public string Addresses => Snapshot.Addresses;
    public string Error => Snapshot.Error;
}
