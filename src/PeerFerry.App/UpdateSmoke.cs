using System.Text.Json;
using Velopack;
using Velopack.Sources;

namespace PeerFerry.App;

// Internal executable-level verification of the real Velopack apply/restart path.
internal static class UpdateSmoke
{
    public static bool TryRun(string[] args)
    {
        if (args.Length != 3 || args[0] is not ("--test-local-update" or "--verify-local-update" or "--test-github-update" or "--verify-github-update")) return false;
        var github = args[0].Contains("github", StringComparison.Ordinal);
        var feed = github ? args[1] : Path.GetFullPath(args[1]); var result = Path.GetFullPath(args[2]);
        Directory.CreateDirectory(Path.GetDirectoryName(result)!);
        try
        {
            var manager = github ? new UpdateManager(new GithubSource(feed, null, false)) : new UpdateManager(feed);
            if (args[0].StartsWith("--verify-", StringComparison.Ordinal))
            {
                File.WriteAllText(result, JsonSerializer.Serialize(new { ok = true, version = manager.CurrentVersion?.ToString(), assemblyVersion = typeof(App).Assembly.GetName().Version?.ToString(), installed = manager.IsInstalled }));
                return true;
            }
            var update = manager.CheckForUpdatesAsync().GetAwaiter().GetResult() ?? throw new InvalidOperationException("Тестовое обновление не найдено.");
            manager.DownloadUpdatesAsync(update).GetAwaiter().GetResult();
            manager.ApplyUpdatesAndRestart(manager.UpdatePendingRestart, [github ? "--verify-github-update" : "--verify-local-update", feed, result]);
        }
        catch (Exception ex) { File.WriteAllText(result, JsonSerializer.Serialize(new { ok = false, error = ex.ToString() })); Environment.ExitCode = 1; }
        return true;
    }
}
