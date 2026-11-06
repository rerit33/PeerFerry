using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using PeerFerry.Core;
using Forms = System.Windows.Forms;

namespace PeerFerry.App;

public partial class MainWindow : Window
{
    private readonly StateStore store = new(App.DataDirectory);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly CancellationTokenSource lifetime = new();
    private CancellationTokenSource? metadataRequest;
    private readonly Forms.NotifyIcon tray;
    private readonly ObservableCollection<TransferRow> transfers = [];
    private readonly ObservableCollection<CatalogRow> catalog = [];
    private readonly List<string> messages = [];
    private MeshNode? node;
    private UpdateService? updates;
    private Task? pendingOperation;
    private bool busy, forceExit, closing, readyToClose, initialized, rendering, refreshingDetails, checkingUpdates, detailsOpen = true;
    private string filter = "All";
    private string? operationMessage;
    private string? metadataHash;
    private byte[]? selectedMetadata;
    private double expandedHeight = 260;
    private record OwnerOption(string Id, string Name);
    public MainWindow()
    {
        InitializeComponent();
        Width = Math.Min(Width, SystemParameters.WorkArea.Width); Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        TransfersGrid.ItemsSource = transfers; CatalogGrid.ItemsSource = catalog;
        CollectionViewSource.GetDefaultView(transfers).Filter = value => value is TransferRow row && MatchesTransfer(row);
        CollectionViewSource.GetDefaultView(catalog).Filter = value => value is CatalogRow row && MatchesCatalog(row);
        OwnerFilter.ItemsSource = new[] { new OwnerOption("", "Все участники") }; OwnerFilter.SelectedIndex = 0;
        using var iconStream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/PeerFerry.ico")).Stream;
        tray = new Forms.NotifyIcon { Text = "PeerFerry", Icon = new System.Drawing.Icon(iconStream) };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть PeerFerry", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => { forceExit = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        timer.Tick += (_, _) => RenderState();
        SetDetails();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public async void ReceiveInvitation(string value)
    {
        ShowFromTray(); if (string.IsNullOrWhiteSpace(value)) return;
        try { if (pendingOperation is not null) await pendingOperation; if (node is not null) await JoinAsync(Invitations.Decode(value)); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "Приглашение PeerFerry"); }
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (initialized) return; initialized = true;
        try
        {
            var settings = store.Load();
            if (App.SmokeDirectory is not null) { settings.Port = FreeTestPort(); settings.DownloadDirectory = Path.Combine(App.DataDirectory, "downloads"); settings.DownloadLimitKiB = 512; settings.ConcurrentDownloads = 1; settings.AutoCheckUpdates = false; }
            node = await Task.Run(async () => { var n = MakeNode(settings); await n.StartAsync(); return n; });
            updates = new(settings.UpdateRepository); tray.Visible = true; timer.Start(); RenderState();
            Log("PeerFerry запущен. Добавьте файл или пригласите друга.");
            if (App.SmokeDirectory is not null) { await SmokeAsync(App.SmokeDirectory); return; }
            var invitation = Environment.GetCommandLineArgs().FirstOrDefault(a => a.StartsWith(Invitations.Prefix, StringComparison.OrdinalIgnoreCase));
            if (invitation is not null) await JoinAsync(Invitations.Decode(invitation));
            if (settings.AutoCheckUpdates) await CheckUpdatesAsync(false);
        }
        catch (Exception ex)
        {
            Log("Не удалось запустить PeerFerry: " + ex.Message);
            if (App.SmokeDirectory is not null) { File.WriteAllText(Path.Combine(App.DataDirectory, "smoke-error.txt"), ex.ToString()); forceExit = true; Close(); }
            else MessageBox.Show(this, ex.Message, "Ошибка запуска PeerFerry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private MeshNode MakeNode(AppSettings settings)
    {
        var n = App.SmokeDirectory is null ? new MeshNode(store, settings) : new MeshNode(store, settings, TestBindings, false);
        n.Notice += message => Dispatcher.BeginInvoke(() => Log(message));
        n.Diagnostic += message => Dispatcher.BeginInvoke(() => Log(message));
        return n;
    }
    private void Log(string message)
    {
        if (messages.LastOrDefault()?.EndsWith(message, StringComparison.Ordinal) == true) return;
        messages.Add($"{DateTime.Now:HH:mm:ss}  {message}"); if (messages.Count > 250) messages.RemoveAt(0);
        NetworkLabel.ToolTip = message;
    }
    private void RenderState()
    {
        if (node is null || closing) return;
        try
        {
            rendering = true;
            var snapshots = node.Torrents.Snapshot(); var remote = node.Catalog();
            Sync(transfers, snapshots, r => r.Hash, s => s.Hash, s => new(s), (r, s) => r.Update(s));
            Sync(catalog, remote, r => r.Hash, s => s.Entry.Hash, s => new(s, snapshots.FirstOrDefault(t => t.Hash == s.Entry.Hash)), (r, s) => r.Update(s, snapshots.FirstOrDefault(t => t.Hash == s.Entry.Hash)));
            var owners = new[] { new OwnerOption("", "Все участники") }.Concat(remote.SelectMany(s => s.Sources).DistinctBy(p => p.Id).OrderBy(p => p.Name).Select(p => new OwnerOption(p.Id, p.Name))).ToArray();
            if (OwnerFilter.ItemsSource is not OwnerOption[] current || !owners.SequenceEqual(current))
            { var selected = OwnerFilter.SelectedValue as string; OwnerFilter.ItemsSource = owners; OwnerFilter.SelectedValue = owners.Any(o => o.Id == selected) ? selected : ""; }
            RefreshFilters();
            FriendsLabel.Text = $"Друзья · {node.PeerSnapshots().Count(p => p.Online)} в сети";
            var connected = node.PeerSnapshots().Any(p => p.Online);
            NetworkLabel.Text = operationMessage ?? (node.Bindings.Count == 0 ? "Ожидание сети · выберите адаптеры в настройках" : string.Join(", ", node.Bindings.Select(b => b.Name).Distinct()) + (connected ? " · Соединение установлено" : " · Ожидание участников"));
            NetworkDot.Fill = (Brush)FindResource(node.Bindings.Count == 0 ? "Muted" : "Accent");
            TotalDownload.Text = "Приём: " + Display.Rate(snapshots.Sum(t => t.DownloadSpeed)); TotalUpload.Text = "Отдача: " + Display.Rate(snapshots.Sum(t => t.UploadSpeed));
            rendering = false; SetDetails(); _ = RefreshParticipantsAsync();
        }
        catch (Exception ex) { rendering = false; Log(ex.Message); }
    }
    private static void Sync<T, TSnapshot>(ObservableCollection<T> items, IReadOnlyList<TSnapshot> snapshots, Func<T, string> key, Func<TSnapshot, string> snapshotKey, Func<TSnapshot, T> create, Action<T, TSnapshot> update)
    {
        var keys = snapshots.Select(snapshotKey).ToHashSet();
        for (var i = items.Count - 1; i >= 0; i--) if (!keys.Contains(key(items[i]))) items.RemoveAt(i);
        foreach (var snapshot in snapshots)
        { var item = items.FirstOrDefault(r => key(r) == snapshotKey(snapshot)); if (item is null) items.Add(create(snapshot)); else update(item, snapshot); }
    }
    private bool MatchesTransfer(TransferRow row) => row.Name.Contains(TransferSearch?.Text.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase) && (filter switch { "Downloading" => row.Snapshot.State is "Downloading" or "Queued" or "Starting" or "Hashing", "Seeding" => row.Snapshot.State == "Seeding", "Paused" => row.Snapshot.State is "Stopped" or "Paused" or "HashingPaused", _ => true });
    private bool MatchesCatalog(CatalogRow row) => row.Name.Contains(CatalogSearch?.Text.Trim() ?? "", StringComparison.CurrentCultureIgnoreCase) && (OwnerFilter?.SelectedValue is not string id || id == "" || row.Share.Sources.Any(p => p.Id == id));
    private void RefreshFilters()
    {
        if (TransfersGrid?.ItemsSource is null) return;
        CollectionViewSource.GetDefaultView(transfers).Refresh(); CollectionViewSource.GetDefaultView(catalog).Refresh();
        TransfersEmpty.Visibility = TransfersGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        CatalogEmpty.Visibility = CatalogGrid.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private void FiltersChanged(object sender, RoutedEventArgs e) { if (!rendering) { RefreshFilters(); if (initialized) SetDetails(); } }
    private void TransferFilterChanged(object sender, RoutedEventArgs e) { filter = (sender as RadioButton)?.Tag as string ?? "All"; if (initialized) { RefreshFilters(); SetDetails(); } }
    private void TabChanged(object sender, SelectionChangedEventArgs e) { if ((ReferenceEquals(e.Source, Tabs) || ReferenceEquals(e.Source, TransferDetails)) && initialized) { SetDetails(); _ = RefreshParticipantsAsync(); } }
    private void TransferSelected(object sender, SelectionChangedEventArgs e) { if (!rendering && initialized) { SetDetails(); _ = RefreshParticipantsAsync(); } }
    private void CatalogSelected(object sender, SelectionChangedEventArgs e) { if (!rendering && initialized) { SetDetails(); _ = LoadMetadataAsync(); } }
    private void SetDetails()
    {
        if (DetailName is null) return;
        var isCatalog = Tabs.SelectedIndex == 0;
        CatalogDetails.Visibility = isCatalog ? Visibility.Visible : Visibility.Collapsed; TransferDetails.Visibility = isCatalog ? Visibility.Collapsed : Visibility.Visible;
        if (!isCatalog && TransfersGrid.SelectedItem is TransferRow transfer)
        {
            DetailName.Text = transfer.Name; DetailSummary.Text = $"Получено {Display.Size(transfer.Snapshot.CompletedBytes)} · Осталось {Display.Size(Math.Max(0, transfer.Snapshot.Size - transfer.Snapshot.CompletedBytes))} · {transfer.RemainingText}";
            GeneralInfo.ItemsSource = new[] { new { Label = "Состояние", Value = transfer.StateText }, new { Label = "Размер", Value = transfer.SizeText }, new { Label = "Получено", Value = transfer.ProgressText }, new { Label = "Папка", Value = transfer.Snapshot.Directory }, new { Label = "Идентификатор", Value = transfer.Hash } };
            if (TransferDetails.SelectedIndex == 2 && detailsOpen) TransferFiles.ItemsSource = node?.Torrents.Files(transfer.Hash).Select(f => new FileRow(f)).ToArray(); DetailDownload.Visibility = Visibility.Collapsed;
        }
        else if (isCatalog && CatalogGrid.SelectedItem is CatalogRow remote)
        {
            DetailName.Text = remote.Name; DetailSummary.Text = $"{remote.SizeText} · {remote.FileCountText} · Папка: {node?.Settings.DownloadDirectory}";
            DetailDownload.Content = remote.Action; DetailDownload.Visibility = Visibility.Visible;
            CatalogParticipants.ItemsSource = node?.CatalogParticipants(remote.Hash).Select(p => new ParticipantRow(p)).ToArray();
            if (metadataHash != remote.Hash) _ = LoadMetadataAsync();
        }
        else
        {
            DetailName.Text = "Выберите раздачу"; DetailSummary.Text = isCatalog ? "Содержимое и участники выбранного файла" : "Состояние, участники и файлы выбранной передачи"; DetailDownload.Visibility = Visibility.Collapsed;
            GeneralInfo.ItemsSource = null; TransferFiles.ItemsSource = null; TransferParticipants.ItemsSource = null; CatalogParticipants.ItemsSource = null; ParticipantsEmpty.Visibility = Visibility.Visible;
            if (isCatalog) { CancelMetadata(); CatalogFiles.ItemsSource = null; ContentMessage.Text = "Выберите раздачу, чтобы посмотреть её содержимое."; ContentNotice.Visibility = Visibility.Visible; RetryContent.Visibility = Visibility.Collapsed; }
        }
    }
    private void CancelMetadata() { metadataRequest?.Cancel(); metadataRequest?.Dispose(); metadataRequest = null; selectedMetadata = null; metadataHash = null; }
    private async Task LoadMetadataAsync()
    {
        if (node is null || CatalogGrid.SelectedItem is not CatalogRow row || metadataHash == row.Hash) return;
        CancelMetadata(); metadataHash = row.Hash; metadataRequest = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = metadataRequest.Token; var owner = node;
        CatalogFiles.ItemsSource = null; ContentMessage.Text = "Получаем список файлов…"; ContentNotice.Visibility = Visibility.Visible; RetryContent.Visibility = Visibility.Collapsed;
        try
        {
            var (bytes, files) = await Task.Run(async () =>
            {
                var metadata = await owner.GetMetadataAsync(row.Share, token);
                token.ThrowIfCancellationRequested();
                return (metadata, TorrentService.DescribeMetadata(metadata, owner.Settings.GroupId));
            }, token);
            if (token.IsCancellationRequested || node != owner || metadataHash != row.Hash) return;
            selectedMetadata = bytes; CatalogFiles.ItemsSource = files.Select(f => new FileRow(f)).ToArray(); ContentNotice.Visibility = Visibility.Collapsed;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { if (!token.IsCancellationRequested && metadataHash == row.Hash) { ContentMessage.Text = ex.Message; RetryContent.Visibility = Visibility.Visible; } }
    }
    private async void RetryMetadata(object sender, RoutedEventArgs e) { CancelMetadata(); await LoadMetadataAsync(); }
    private async Task RefreshParticipantsAsync()
    {
        if (refreshingDetails || node is null || closing || Tabs.SelectedIndex != 1 || TransfersGrid.SelectedItem is not TransferRow row) return;
        refreshingDetails = true; var owner = node;
        try
        {
            var participants = await owner.TransferParticipantsAsync(row.Hash);
            if (node != owner || TransfersGrid.SelectedItem is not TransferRow current || current.Hash != row.Hash || closing) return;
            TransferParticipants.ItemsSource = participants.Select(p => new ParticipantRow(p)).ToArray(); ParticipantsEmpty.Visibility = participants.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (Exception ex) { if (!closing) Log(ex.Message); }
        finally { refreshingDetails = false; }
    }
    private void ToggleDetails(object sender, RoutedEventArgs e)
    {
        if (detailsOpen) expandedHeight = DetailsRow.ActualHeight;
        detailsOpen = !detailsOpen; DetailBody.Visibility = detailsOpen ? Visibility.Visible : Visibility.Collapsed;
        DetailsRow.Height = detailsOpen ? new GridLength(Math.Max(180, expandedHeight)) : GridLength.Auto;
        DetailsSplitter.IsEnabled = detailsOpen; DetailsChevron.Kind = detailsOpen ? "ChevronDown" : "ChevronUp";
        if (detailsOpen) SetDetails();
    }
    private async Task RunAsync(string message, Func<Task> action)
    {
        if (node is null || busy || closing) return;
        busy = true; operationMessage = message; NetworkLabel.Text = message; Log(message); Cursor = Cursors.AppStarting;
        try { pendingOperation = Task.Run(action); await pendingOperation; RenderState(); }
        catch (OperationCanceledException) { Log("Операция отменена."); }
        catch (Exception ex) { Log(ex.Message); if (!closing) MessageBox.Show(this, ex.Message, "PeerFerry", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { pendingOperation = null; busy = false; operationMessage = null; Cursor = null; RenderState(); }
    }
    private async void AddFiles(object sender, RoutedEventArgs e)
    { if (busy) return; var dialog = new OpenFileDialog { Title = "Добавить файлы в PeerFerry", Multiselect = true }; if (dialog.ShowDialog(this) == true) await PublishAsync(dialog.FileNames); }
    private async void AddFolder(object sender, RoutedEventArgs e)
    { if (busy) return; var dialog = new OpenFolderDialog { Title = "Добавить папку в PeerFerry" }; if (dialog.ShowDialog(this) == true) await PublishAsync([dialog.FolderName]); }
    private async Task PublishAsync(IEnumerable<string> paths)
    {
        await RunAsync("Подготовка раздачи: чтение файлов и расчёт контрольных сумм…", async () =>
        { foreach (var path in paths) { await node!.Torrents.PublishAsync(path, lifetime.Token); _ = Dispatcher.BeginInvoke(() => Log($"Добавлено: {Path.GetFileName(path)}")); } });
        Tabs.SelectedIndex = 1;
    }
    private void OnDragOver(object sender, DragEventArgs e) { e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) && !busy ? DragDropEffects.Copy : DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, DragEventArgs e) { if (!busy && e.Data.GetData(DataFormats.FileDrop) is string[] paths) await PublishAsync(paths); }
    private void ShowAddMenu(object sender, RoutedEventArgs e) { if (sender is Button { ContextMenu: { } menu } button) { menu.PlacementTarget = button; menu.IsOpen = true; } }
    private void ShowTransferMenu(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: TransferRow row } button) { TransfersGrid.SelectedItem = row; ShowAddMenu(button, e); } }
    private void CreateInvitation(object sender, RoutedEventArgs e)
    {
        if (node is null || busy) return;
        try { new LinkWindow("Пригласить в PeerFerry", "Отправьте приглашение другу. Оно даёт доступ к вашей группе и её раздачам. Должна работать общая LAN/VPN-сеть.", node.CreateInvitation(), true) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { MessageBox.Show(this, ex.Message, "PeerFerry"); }
    }
    private async void JoinGroup(object sender, RoutedEventArgs e)
    {
        if (busy || node is null) return;
        var dialog = new LinkWindow("Присоединиться к группе", "Вставьте приглашение. При переходе в другую группу текущие передачи остановятся; файлы сохранятся на диске.") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await JoinAsync(Invitations.Decode(dialog.Value)); } catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "PeerFerry"); }
    }
    private async Task JoinAsync(Invitation invitation)
    {
        if (node is null) return; CancelMetadata();
        await RunAsync("Подключение к группе…", async () =>
        {
            if (node.Settings.GroupKey == invitation.GroupKey) { await node.RememberPeerAsync(invitation.Peer); await node.RefreshAsync(lifetime.Token); return; }
            var settings = node.Settings; await node.DisposeAsync(); settings.GroupKey = invitation.GroupKey; settings.GroupName = invitation.GroupName; settings.KnownPeers = [invitation.Peer];
            await store.SaveAsync(settings); node = MakeNode(settings); await node.StartAsync(); await node.RefreshAsync(lifetime.Token);
        }); Tabs.SelectedIndex = 0;
    }
    private async void RefreshPeers(object sender, RoutedEventArgs e) => await RunAsync("Поиск участников…", () => node!.RefreshAsync(lifetime.Token));
    private async Task DownloadRowAsync(CatalogRow row)
    {
        if (row.Local is not null)
        { if (row.Local.Progress >= 100) OpenFolder(row.Local.Directory); else { Tabs.SelectedIndex = 1; FilterAll.IsChecked = true; TransferSearch.Clear(); TransfersGrid.SelectedItem = transfers.FirstOrDefault(t => t.Hash == row.Hash); } return; }
        var metadata = metadataHash == row.Hash ? selectedMetadata : null;
        await RunAsync("Запуск загрузки…", () => node!.DownloadAsync(row.Share, lifetime.Token, metadata));
        Tabs.SelectedIndex = 1; FilterAll.IsChecked = true; TransferSearch.Clear(); TransfersGrid.SelectedItem = transfers.FirstOrDefault(t => t.Hash == row.Hash);
    }
    private async void CatalogAction(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: CatalogRow row }) { CatalogGrid.SelectedItem = row; await DownloadRowAsync(row); } }
    private async void DownloadSelected(object sender, RoutedEventArgs e) { if (CatalogGrid.SelectedItem is CatalogRow row) await DownloadRowAsync(row); }
    private async void CatalogDoubleClick(object sender, MouseButtonEventArgs e) { if (e.OriginalSource is DependencyObject source && FindParent<DataGridRow>(source) is { DataContext: CatalogRow row } && FindParent<Button>(source) is null) await DownloadRowAsync(row); }
    private static T? FindParent<T>(DependencyObject child) where T : DependencyObject { for (var current = child; current is not null; current = VisualTreeHelper.GetParent(current)) if (current is T result) return result; return null; }
    private async void ToggleTransfer(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: TransferRow row }) { TransfersGrid.SelectedItem = row; await RunAsync("Изменение передачи…", () => row.CanResume ? node!.Torrents.ResumeAsync(row.Hash) : node!.Torrents.PauseAsync(row.Hash)); } }
    private async void RemoveTransfer(object sender, RoutedEventArgs e)
    { if (TransfersGrid.SelectedItem is TransferRow row) await RunAsync("Удаление передачи из списка…", () => node!.Torrents.RemoveAsync(row.Hash)); }
    private void OpenTransferFolder(object sender, RoutedEventArgs e) { if (sender is Button { DataContext: TransferRow row }) OpenFolder(row.Snapshot.Directory); }
    private void OpenFolder(string path)
    { try { if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", path) { UseShellExecute = true }); else Log("Папка пока не создана: " + path); } catch (Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "PeerFerry"); } }
    private void ShowFriends(object sender, RoutedEventArgs e)
    {
        if (node is null) return;
        var list = new ListView(); var view = new GridView();
        view.Columns.Add(new GridViewColumn { Header = "Участник", DisplayMemberBinding = new Binding("Name"), Width = 230 });
        view.Columns.Add(new GridViewColumn { Header = "Состояние", DisplayMemberBinding = new Binding("Status"), Width = 150 });
        view.Columns.Add(new GridViewColumn { Header = "Подключение", DisplayMemberBinding = new Binding("Addresses"), Width = 260 }); list.View = view;
        void Refresh() => list.ItemsSource = node?.PeerSnapshots().Select(p => new { p.Name, Status = p.Online ? "В сети" : "Нет связи", p.Addresses }).ToArray();
        var dialog = AuxiliaryWindow("Друзья · " + node.Settings.GroupName, list, "Пригласите друзей в вашу группу с помощью ссылки на верхней панели.");
        var refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) }; refresh.Tick += (_, _) => Refresh(); dialog.Closed += (_, _) => refresh.Stop(); Refresh(); refresh.Start(); dialog.ShowDialog();
    }
    private Window AuxiliaryWindow(string title, UIElement content, string hint)
    {
        var panel = new DockPanel { Margin = new Thickness(22) }; var description = new TextBlock { Text = hint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 18) }; DockPanel.SetDock(description, Dock.Top); panel.Children.Add(description);
        var close = new Button { Content = "Закрыть", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(close, Dock.Bottom); panel.Children.Add(close); panel.Children.Add(content);
        return new Window { Title = title, Owner = this, Width = 760, Height = 430, MinWidth = 560, MinHeight = 300, WindowStartupLocation = WindowStartupLocation.CenterOwner, Content = panel };
    }
    private void ShowDiagnostics(object sender, RoutedEventArgs e)
    {
        var text = new TextBox { Text = string.Join(Environment.NewLine, messages), IsReadOnly = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        AuxiliaryWindow("Сетевые сведения и журнал", text, $"PeerFerry {typeof(App).Assembly.GetName().Version?.ToString(3)} · {node?.Settings.DisplayName}\n" + string.Join("\n", node?.Bindings.Select(b => $"{b.Name} · {b.Address}") ?? [])).ShowDialog();
    }
    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (node is null || busy) return;
        var dialog = new SettingsWindow(node.Settings, () => CheckUpdatesAsync(true), () => updates?.Ready == true) { Owner = this };
        if (dialog.ShowDialog() != true || closing) return; CancelMetadata();
        await RunAsync("Применение настроек и восстановление передач…", async () =>
        { var old = node.Settings; await node.DisposeAsync(); dialog.Result.KnownPeers = old.KnownPeers; dialog.Result.Shares = old.Shares; await store.SaveAsync(dialog.Result); node = MakeNode(dialog.Result); await node.StartAsync(); });
        updates = new(dialog.Result.UpdateRepository);
    }
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (busy || checkingUpdates || node is null || updates is null) return;
        if (updates.Ready)
        {
            if (!manual) return;
            if (MessageBox.Show(this, "Сохранить передачи, установить обновление и перезапустить PeerFerry?", "Обновление PeerFerry", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            CancelMetadata(); await RunAsync("Сохранение передач перед обновлением…", async () => { await node.DisposeAsync(); node = null; }); updates.ApplyAndRestart(); return;
        }
        checkingUpdates = true;
        try { var message = await Task.Run(() => updates.CheckAndDownloadAsync(progress => Dispatcher.BeginInvoke(() => Log($"Загрузка обновления: {progress}%")))); Log(message); if (manual) MessageBox.Show(message, "Обновления PeerFerry"); }
        catch (Exception ex) { Log("Обновления пока недоступны: " + ex.Message); if (manual) MessageBox.Show("Пока нет доступного релиза или GitHub недоступен.\n" + ex.Message, "Обновления PeerFerry"); }
        finally { checkingUpdates = false; }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (readyToClose) return; e.Cancel = true;
        if (!forceExit && node?.Settings.MinimizeToTray == true && App.SmokeDirectory is null) { Hide(); return; }
        if (closing) return; closing = true; IsEnabled = false; timer.Stop(); CancelMetadata(); await lifetime.CancelAsync();
        if (pendingOperation is not null) { try { await pendingOperation; } catch (Exception) { } }
        try { if (node is not null) await Task.Run(async () => await node.DisposeAsync()); }
        catch (Exception ex) { File.AppendAllText(Path.Combine(App.DataDirectory, "errors.log"), ex + Environment.NewLine); }
        tray.Visible = false; tray.Dispose(); readyToClose = true; Close();
    }
}
