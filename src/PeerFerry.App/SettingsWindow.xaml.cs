using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PeerFerry.Core;

namespace PeerFerry.App;

public partial class SettingsWindow : Window
{
    public sealed class AdapterRow : INotifyPropertyChanged
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";
        public string Addresses { get; init; } = "";
        public string Description { get; init; } = "";
        public bool IsVpn { get; init; }
        public string Icon => IsVpn ? "Network" : Name.Contains("Wi", StringComparison.OrdinalIgnoreCase) ? "Wifi" : "Network";
        public bool Manual { get; private set; }
        private bool selected;
        public bool ManualSelection { get; private set; }
        public bool Selected { get => selected; set { selected = value; if (Manual) ManualSelection = value; PropertyChanged?.Invoke(this, new(nameof(Selected))); } }
        public void SetMode(string mode) { Manual = mode == "Manual"; selected = Manual ? ManualSelection : mode == "All" || IsVpn; PropertyChanged?.Invoke(this, new(null)); }
        public void Initialize(bool value) { ManualSelection = value; }
        public event PropertyChangedEventHandler? PropertyChanged;
    }
    private readonly List<AdapterRow> rows = [];
    private readonly Func<Task>? checkUpdates;
    private readonly Func<bool>? updateReady;
    public AppSettings Result { get; }
    public SettingsWindow(AppSettings settings, Func<Task>? checkUpdates = null, Func<bool>? updateReady = null)
    {
        Result = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings, StateStore.Json), StateStore.Json)!;
        this.checkUpdates = checkUpdates; this.updateReady = updateReady; InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        rows = NetworkAdapters.Enumerate().GroupBy(b => b.AdapterId).Select(g => new AdapterRow { Id = g.Key, Name = g.First().Name, Description = g.First().Description, Addresses = string.Join(", ", g.Select(b => b.Address)), IsVpn = g.First().IsVpn }).ToList();
        foreach (var row in rows) row.Initialize(settings.AdapterIds.Contains(row.Id));
        AdaptersGrid.ItemsSource = rows;
        NameBox.Text = settings.DisplayName; PortBox.Text = settings.Port.ToString(); DownloadBox.Text = settings.DownloadDirectory;
        DownloadLimitBox.Text = (settings.DownloadLimitKiB > 0 ? settings.DownloadLimitKiB : 10240).ToString(); UploadLimitBox.Text = (settings.UploadLimitKiB > 0 ? settings.UploadLimitKiB : 10240).ToString();
        DownloadLimitEnabled.IsChecked = settings.DownloadLimitKiB > 0; UploadLimitEnabled.IsChecked = settings.UploadLimitKiB > 0; LimitsChanged(this, new());
        ConcurrencyBox.Text = settings.ConcurrentDownloads.ToString(); TrayBox.IsChecked = settings.MinimizeToTray; UpdatesBox.IsChecked = settings.AutoCheckUpdates; RepositoryBox.Text = settings.UpdateRepository;
        VersionLabel.Text = $"Установлена версия {typeof(App).Assembly.GetName().Version?.ToString(3)}";
        CheckUpdateButton.IsEnabled = checkUpdates is not null;
        if (updateReady?.Invoke() == true) CheckUpdateButton.Content = "Установить и перезапустить";
        (settings.NetworkMode switch { "Manual" => ManualMode, "All" => AllMode, _ => AutoMode }).IsChecked = true;
    }
    public void SelectSection(int index) => SettingsTabs.SelectedIndex = index;
    internal void SaveForSmoke() => Save(this, new());
    private string Mode => ManualMode.IsChecked == true ? "Manual" : AllMode.IsChecked == true ? "All" : "Auto";
    private void ModeChanged(object sender, RoutedEventArgs e)
    {
        if (AdaptersGrid is null) return;
        foreach (var row in rows) row.SetMode(Mode);
        var offlineCount = Result.AdapterIds.Except(rows.Select(r => r.Id)).Count();
        AdapterHint.Text = (Mode switch { "Manual" => "Отметьте один или несколько адаптеров. Скрытые и отключённые выбранные адаптеры сохраняются.", "All" => "Используются все активные IPv4-адаптеры, включая обычную локальную сеть.", _ => "Автоматически используются только активные адаптеры ZeroTier и Radmin VPN." }) + (offlineCount > 0 ? $" Сейчас недоступно выбранных адаптеров: {offlineCount}." : "");
    }
    private void LimitsChanged(object sender, RoutedEventArgs e)
    {
        if (DownloadHint is null || UploadHint is null) return;
        static string Hint(bool enabled, string text) => !enabled ? "Без ограничения" : int.TryParse(text, out var value) && value > 0 ? $"≈ {value / 1024.0:0.##} MB/s" : "Введите целое число больше нуля.";
        DownloadHint.Text = Hint(DownloadLimitEnabled.IsChecked == true, DownloadLimitBox.Text); UploadHint.Text = Hint(UploadLimitEnabled.IsChecked == true, UploadLimitBox.Text);
    }
    private void StepConcurrency(object sender, RoutedEventArgs e) { if (sender is Button button) { _ = int.TryParse(ConcurrencyBox.Text, out var value); ConcurrencyBox.Text = Math.Clamp(value + int.Parse((string)button.Tag), 1, 20).ToString(); } }
    private void BrowseDownloads(object sender, RoutedEventArgs e) { var dialog = new OpenFolderDialog { Title = "Папка загрузок PeerFerry" }; if (dialog.ShowDialog(this) == true) DownloadBox.Text = dialog.FolderName; }
    private async void CheckUpdates(object sender, RoutedEventArgs e)
    {
        if (checkUpdates is null) return; CheckUpdateButton.IsEnabled = false; UpdateStatus.Text = "Проверяем обновления…";
        try { await checkUpdates(); UpdateStatus.Text = "Проверка завершена."; }
        finally { CheckUpdateButton.IsEnabled = true; if (updateReady?.Invoke() == true) { CheckUpdateButton.Content = "Установить и перезапустить"; UpdateStatus.Text = "Обновление скачано и готово к установке."; } }
    }
    private void Save(object sender, RoutedEventArgs e)
    {
        try
        {
            AdaptersGrid.CommitEdit(); Result.DisplayName = NameBox.Text.Trim(); Result.NetworkMode = Mode;
            Result.AdapterIds = rows.Where(r => r.ManualSelection).Select(r => r.Id).Concat(Result.AdapterIds.Except(rows.Select(r => r.Id))).Distinct().ToList();
            Result.Port = int.Parse(PortBox.Text); if (string.IsNullOrWhiteSpace(DownloadBox.Text) || !Path.IsPathFullyQualified(DownloadBox.Text)) throw new InvalidDataException("Укажите полный путь к папке загрузок.");
            Result.DownloadDirectory = Path.GetFullPath(DownloadBox.Text);
            static int Limit(CheckBox enabled, TextBox input) { if (enabled.IsChecked != true) return 0; if (!int.TryParse(input.Text, out var value) || value <= 0) throw new InvalidDataException("Лимит скорости должен быть целым числом больше нуля."); return value; }
            Result.DownloadLimitKiB = Limit(DownloadLimitEnabled, DownloadLimitBox); Result.UploadLimitKiB = Limit(UploadLimitEnabled, UploadLimitBox); Result.ConcurrentDownloads = int.Parse(ConcurrencyBox.Text);
            Result.MinimizeToTray = TrayBox.IsChecked == true; Result.AutoCheckUpdates = UpdatesBox.IsChecked == true;
            var repo = RepositoryBox.Text.Trim().TrimEnd('/');
            if (!Uri.TryCreate(repo, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Host != "github.com" || uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries).Length != 2 || !string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment)) throw new InvalidDataException("Укажите адрес https://github.com/владелец/репозиторий.");
            Result.UpdateRepository = repo; StateStore.Validate(Result); DialogResult = true;
        }
        catch (Exception ex) when (ex is FormatException or OverflowException or ArgumentException or InvalidDataException or NotSupportedException) { ErrorLabel.Text = ex.Message; }
    }
}
