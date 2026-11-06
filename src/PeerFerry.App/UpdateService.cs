using Velopack;
using Velopack.Sources;

namespace PeerFerry.App;

internal sealed class UpdateService(string repository)
{
    private readonly UpdateManager manager = new(new GithubSource(repository, null, false));
    private UpdateInfo? available;
    public bool Ready => manager.UpdatePendingRestart is not null;
    public async Task<string> CheckAndDownloadAsync(Action<int> progress)
    {
        if (!manager.IsInstalled) return "Для автообновлений запустите установленную версию PeerFerry или приложение из полного portable-архива.";
        available = await manager.CheckForUpdatesAsync();
        if (available is null) return "Установлена актуальная версия PeerFerry.";
        await manager.DownloadUpdatesAsync(available, progress);
        return "Обновление скачано. Нажмите «Обновления», чтобы перезапустить приложение.";
    }
    public void ApplyAndRestart() => manager.ApplyUpdatesAndRestart(manager.UpdatePendingRestart);
}
