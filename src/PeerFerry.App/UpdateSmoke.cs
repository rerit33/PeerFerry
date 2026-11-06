using System.Text.Json;
using Velopack;

namespace PeerFerry.App;

// Internal executable-level verification of the real Velopack apply/restart path.
internal static class UpdateSmoke
{
    public static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("--test-local-update" or "--verify-local-update")) return false;
        var feed = Path.GetFullPath(args[1]); var result = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        try
        {
            var manager = new UpdateManager(feed);
            if (args[0] == "--verify-local-update")
            {
                File.WriteAllText(result, JsonSerializer.Serialize(new { ok = true, version = manager.CurrentVersion?.ToString(), assemblyVersion = typeof(App).Assembly.GetName().Version?.ToString(), installed = manager.IsInstalled }));
                return true;
            }
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult() ?? throw new InvalidOperationException("Тестовое обновление не найдено.");
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            manager.ApplyUpdatesAndRestart(manager.UpdatePendingRestart, ["--verify-local-update", feed, result]);
        }
        catch (Exception ex) { File.WriteAllText(result, JsonSerializer.Serialize(new { ok = false, error = ex.ToString() })); Environment.ExitCode = 1; }
        return true;
    }
}
