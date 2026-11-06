using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using Velopack;

namespace PeerFerry.App;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        VelopackApp.Build().Run();
        if (UpdateSmoke.TryRun(args)) return;
        var app = new App(); app.InitializeComponent(); app.Run();
    }
}
public partial class App : System.Windows.Application
{
    private Mutex? mutex;
    private readonly CancellationTokenSource lifetime = new();
    public static string DataDirectory { get; private set; } = "";
    public static string? SmokeDirectory { get; private set; }
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        var dataIndex = Array.IndexOf(e.Args, "--data-dir");
        DataDirectory = dataIndex >= 0 && dataIndex + 1 < e.Args.Length ? Path.GetFullPath(e.Args[dataIndex + 1]) : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PeerFerry");
        var smokeIndex = Array.IndexOf(e.Args, "--smoke-test");
        if (smokeIndex >= 0 && smokeIndex + 1 < e.Args.Length) SmokeDirectory = Path.GetFullPath(e.Args[smokeIndex + 1]);
        Directory.CreateDirectory(DataDirectory);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(DataDirectory.ToUpperInvariant())))[..24];
        mutex = new Mutex(true, "Local\\PeerFerry-" + suffix, out var created);
        var pipeName = "PeerFerry-" + suffix;
        if (!created)
        {
            var invitation = e.Args.FirstOrDefault(a => a.StartsWith(Core.Invitations.Prefix, StringComparison.OrdinalIgnoreCase)) ?? "";
            DeepLinks.Forward(pipeName, invitation); Shutdown(); return;
        }
        DispatcherUnhandledException += (_, args) =>
        {
            File.AppendAllText(Path.Combine(DataDirectory, "errors.log"), $"{DateTimeOffset.Now:O} {args.Exception}\n");
            System.Windows.MessageBox.Show(args.Exception.Message, "PeerFerry", MessageBoxButton.OK, MessageBoxImage.Error); args.Handled = true;
        };
        MainWindow = new MainWindow(); MainWindow.Show();
        if (SmokeDirectory is null)
        {
            try { DeepLinks.Register(); } catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException) { }
            _ = Task.Run(() => DeepLinks.ListenAsync(pipeName, value => Dispatcher.BeginInvoke(() => ((MainWindow)MainWindow).ReceiveInvitation(value)), lifetime.Token));
        }
    }
    protected override void OnExit(ExitEventArgs e) { lifetime.Cancel(); mutex?.Dispose(); base.OnExit(e); }
}
