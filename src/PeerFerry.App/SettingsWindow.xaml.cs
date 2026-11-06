using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PeerFerry.Core;

namespace PeerFerry.App;

public partial class SettingsWindow : Window
{
    public sealed class AdapterRow
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Addresses { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsVpn { get; init; }
        public bool Selected { get; set; }
    }
    private readonly List<AdapterRow> rows;
    public AppSettings Result { get; }
    public SettingsWindow(AppSettings settings)
    {
        Result = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, StateStore.Json), StateStore.Json)!;
        InitializeComponent();
        rows = NetworkAdapters.Enumerate().GroupBy(b => b.AdapterId).Select(g => new AdapterRow
        { Id = g.Key, Name = g.First().Name, Description = g.First().Description, Addresses = string.Join(", ", g.Select(b => b.Address)), IsVpn = g.First().IsVpn, Selected = settings.AdapterIds.Contains(g.Key) }).ToList();
        AdaptersGrid.ItemsSource = rows;
        NameBox.Text = settings.DisplayName; PortBox.Text = settings.Port.ToString(); DownloadBox.Text = settings.DownloadDirectory;
        DownloadLimitBox.Text = settings.DownloadLimitKiB.ToString(); UploadLimitBox.Text = settings.UploadLimitKiB.ToString(); ConcurrencyBox.Text = settings.ConcurrentDownloads.ToString();
        TrayBox.IsChecked = settings.MinimizeToTray; UpdatesBox.IsChecked = settings.AutoCheckUpdates; RepositoryBox.Text = settings.UpdateRepository;
        ModeBox.SelectedIndex = settings.NetworkMode switch { "Manual" => 1, "All" => 2, _ => 0 };
    }
    private void ModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (AdaptersGrid is null || rows is null) return;
        var mode = (ModeBox.SelectedItem as ComboBoxItem)?.Tag as string;
        AdaptersGrid.IsEnabled = mode == "Manual";
        if (mode != "Manual") { foreach (var row in rows) row.Selected = mode == "All" || row.IsVpn; AdaptersGrid.Items.Refresh(); }
    }
    private void BrowseDownloads(object sender, RoutedEventArgs e)
    { var dialog = new OpenFolderDialog { Title = "Папка загрузок PeerFerry" }; if (dialog.ShowDialog(this) == true) DownloadBox.Text = dialog.FolderName; }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            Result.DisplayName = NameBox.Text.Trim(); Result.NetworkMode = (string)((ComboBoxItem)ModeBox.SelectedItem).Tag;
            Result.AdapterIds = rows.Where(r => r.Selected).Select(r => r.Id).Concat(Result.AdapterIds.Except(rows.Select(r => r.Id))).Distinct().ToList();
            Result.Port = int.Parse(PortBox.Text); Result.DownloadDirectory = Path.GetFullPath(DownloadBox.Text);
            Result.DownloadLimitKiB = int.Parse(DownloadLimitBox.Text); Result.UploadLimitKiB = int.Parse(UploadLimitBox.Text); Result.ConcurrentDownloads = int.Parse(ConcurrencyBox.Text);
            Result.MinimizeToTray = TrayBox.IsChecked == true; Result.AutoCheckUpdates = UpdatesBox.IsChecked == true;
            var repo = RepositoryBox.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(repo, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query))
                throw new InvalidDataException("Укажите адрес https://github.com/владелец/репозиторий.");
            Result.UpdateRepository = repo; StateStore.Validate(Result); DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidDataException) { ErrorLabel.Text = ex.Message; }
    }
}
