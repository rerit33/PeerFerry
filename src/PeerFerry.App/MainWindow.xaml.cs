using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
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
    private readonly Forms.NotifyIcon tray;
    private MeshNode? node;
    private UpdateService? updates;
    private Task? pendingOperation;
    private bool busy, forceExit, closing, readyToClose;
    private readonly List<string> messages = [];
    public MainWindow()
    {
        InitializeComponent();
        VersionLabel.Text = $"{typeof(App).Assembly.GetName().Version?.ToString(3)} · предварительная версия";
        tray = new Forms.NotifyIcon { Text = "PeerFerry", Icon = System.Drawing.SystemIcons.Application };
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("Открыть PeerFerry", null, (_, _) => Dispatcher.Invoke(ShowFromTray));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.Invoke(() => { forceExit = true; Close(); }));
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
        timer.Tick += (_, _) => RenderState();
    }
    private void ShowFromTray() { Show(); WindowState = WindowState.Normal; Activate(); }
    public async void ReceiveInvitation(string value)
    {
        ShowFromTray(); if (string.IsNullOrWhiteSpace(value)) return;
        try
        {
            if (pendingOperation is not null) await pendingOperation;
            if (node is not null) await JoinAsync(Invitations.Decode(value));
        }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "Приглашение PeerFerry"); }
    }
    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            var settings = store.Load();
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
            else System.Windows.MessageBox.Show(this, ex.Message, "Ошибка запуска PeerFerry", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
    private MeshNode MakeNode(AppSettings settings)
    {
        var n = new MeshNode(store, settings);
        n.Notice += message => Dispatcher.BeginInvoke(() => Log(message));
        n.Diagnostic += message => Dispatcher.BeginInvoke(() => Log(message, false));
        return n;
    }
    private void Log(string message, bool showStatus = true)
    {
        if (messages.LastOrDefault()?.EndsWith(message, StringComparison.Ordinal) == true) return;
        messages.Add($"{DateTime.Now:HH:mm:ss}  {message}");
        if (messages.Count > 250) messages.RemoveAt(0);
        LogBox.Text = string.Join(Environment.NewLine, messages); LogBox.ScrollToEnd();
        if (showStatus) StatusLabel.Text = message;
    }
    private void RenderState()
    {
        if (node is null || closing) return;
        try
        {
            var transfers = node.Torrents.Snapshot().Select(t => new TransferRow(t)).ToList();
            Sync(MineGrid, transfers.Where(t => t.Snapshot.IsPublication).ToList(), t => t.Hash);
            Sync(TransfersGrid, transfers, t => t.Hash);
            RenderCatalog();
            Sync(PeersGrid, node.PeerSnapshots().Select(p => new PeerRow(p)).ToList(), p => p.Id);
            GroupLabel.Text = $"{node.Settings.DisplayName} · группа «{node.Settings.GroupName}» · {node.PeerSnapshots().Count(p => p.Online)} в сети";
            NetworkLabel.Text = node.Bindings.Count == 0 ? "Сеть не выбрана. Откройте Настройки → Сеть." : string.Join("   |   ", node.Bindings.Select(b => $"{b.Name}: {b.Address}"));
        }
        catch (Exception ex) { Log(ex.Message); }
    }
    private static void Sync<T>(DataGrid grid, List<T> rows, Func<T, string> key)
    {
        var selected = grid.SelectedItem is T selection ? key(selection) : null;
        if (grid.ItemsSource is not ObservableCollection<T> items) { items = []; grid.ItemsSource = items; }
        for (var i = 0; i < rows.Count; i++) { if (i >= items.Count) items.Add(rows[i]); else if (!Equals(items[i], rows[i])) items[i] = rows[i]; }
        while (items.Count > rows.Count) items.RemoveAt(items.Count - 1);
        if (selected is not null) grid.SelectedItem = items.FirstOrDefault(r => key(r) == selected);
    }
    private void RenderCatalog()
    {
        if (node is null || SearchBox is null) return;
        var term = SearchBox.Text.Trim();
        Sync(CatalogGrid, node.Catalog().Where(f => f.Entry.Name.Contains(term, StringComparison.CurrentCultureIgnoreCase)).Select(f => new CatalogRow(f)).ToList(), f => f.Hash);
    }
    private void SearchChanged(object sender, TextChangedEventArgs e) => RenderCatalog();
    private async Task RunAsync(string message, Func<Task> action)
    {
        if (node is null || busy || closing) return;
        busy = true; StatusLabel.Text = message; Cursor = Cursors.AppStarting;
        try { pendingOperation = Task.Run(action); await pendingOperation; RenderState(); }
        catch (OperationCanceledException) { Log("Операция отменена."); }
        catch (Exception ex) { Log(ex.Message); if (!closing) System.Windows.MessageBox.Show(this, ex.Message, "PeerFerry", MessageBoxButton.OK, MessageBoxImage.Information); }
        finally { pendingOperation = null; busy = false; Cursor = null; }
    }
    private async void AddFiles(object sender, RoutedEventArgs e)
    {
        if (busy) return;
        var dialog = new OpenFileDialog { Title = "Добавить файлы в PeerFerry", Multiselect = true };
        if (dialog.ShowDialog(this) == true) await PublishAsync(dialog.FileNames);
    }
    private async void AddFolder(object sender, RoutedEventArgs e)
    { if (busy) return; var dialog = new OpenFolderDialog { Title = "Добавить папку в PeerFerry" }; if (dialog.ShowDialog(this) == true) await PublishAsync([dialog.FolderName]); }
    private async Task PublishAsync(IEnumerable<string> paths)
    {
        await RunAsync("Подготовка раздачи: чтение файлов и расчёт контрольных сумм…", async () =>
        { foreach (var path in paths) { await node!.Torrents.PublishAsync(path, lifetime.Token); _ = Dispatcher.BeginInvoke(() => Log($"Добавлено: {Path.GetFileName(path)}")); } });
    }
    private void OnDragOver(object sender, System.Windows.DragEventArgs e) { e.Effects = e.Data.GetDataPresent(System.Windows.DataFormats.FileDrop) && !busy ? System.Windows.DragDropEffects.Copy : System.Windows.DragDropEffects.None; e.Handled = true; }
    private async void OnDrop(object sender, System.Windows.DragEventArgs e) { if (!busy && e.Data.GetData(System.Windows.DataFormats.FileDrop) is string[] paths) await PublishAsync(paths); }
    private void CreateInvitation(object sender, RoutedEventArgs e)
    {
        if (node is null || busy) return;
        try { new LinkWindow("Пригласить в PeerFerry", "Отправьте приглашение другу. Оно даёт доступ к вашей группе и её раздачам. Должна работать общая LAN/VPN-сеть.", node.CreateInvitation(), true) { Owner = this }.ShowDialog(); }
        catch (Exception ex) { System.Windows.MessageBox.Show(this, ex.Message, "PeerFerry"); }
    }
    private async void JoinGroup(object sender, RoutedEventArgs e)
    {
        if (busy || node is null) return;
        var dialog = new LinkWindow("Присоединиться к группе", "Вставьте приглашение. При переходе в другую группу текущие передачи остановятся; файлы сохранятся на диске.") { Owner = this };
        if (dialog.ShowDialog() != true) return;
        try { await JoinAsync(Invitations.Decode(dialog.Value)); }
        catch (Exception ex) { Log(ex.Message); System.Windows.MessageBox.Show(this, ex.Message, "PeerFerry"); }
    }
    private async Task JoinAsync(Invitation invitation)
    {
        if (node is null) return;
        await RunAsync("Подключение к группе…", async () =>
        {
            if (node.Settings.GroupKey == invitation.GroupKey) { await node.RememberPeerAsync(invitation.Peer); await node.RefreshAsync(lifetime.Token); return; }
            var settings = node.Settings;
            await node.DisposeAsync(); settings.GroupKey = invitation.GroupKey; settings.GroupName = invitation.GroupName; settings.KnownPeers = [invitation.Peer];
            await store.SaveAsync(settings); node = MakeNode(settings); await node.StartAsync(); await node.RefreshAsync(lifetime.Token);
        });
        Tabs.SelectedIndex = 1;
    }
    private async void RefreshPeers(object sender, RoutedEventArgs e) => await RunAsync("Поиск участников…", () => node!.RefreshAsync(lifetime.Token));
    private async void DownloadSelected(object sender, RoutedEventArgs e)
    {
        if (CatalogGrid.SelectedItem is not CatalogRow row) return;
        await RunAsync("Запуск загрузки…", () => node!.DownloadAsync(row.Share, lifetime.Token)); Tabs.SelectedIndex = 2;
    }
    private async Task TransferAction(DataGrid grid, string operation)
    {
        if (grid.SelectedItem is not TransferRow row) return;
        await RunAsync("Изменение передачи…", () => operation switch { "pause" => node!.Torrents.PauseAsync(row.Hash), "resume" => node!.Torrents.ResumeAsync(row.Hash), _ => node!.Torrents.RemoveAsync(row.Hash) });
    }
    private async void PauseMine(object sender, RoutedEventArgs e) => await TransferAction(MineGrid, "pause");
    private async void ResumeMine(object sender, RoutedEventArgs e) => await TransferAction(MineGrid, "resume");
    private async void RemoveMine(object sender, RoutedEventArgs e) => await TransferAction(MineGrid, "remove");
    private async void PauseTransfer(object sender, RoutedEventArgs e) => await TransferAction(TransfersGrid, "pause");
    private async void ResumeTransfer(object sender, RoutedEventArgs e) => await TransferAction(TransfersGrid, "resume");
    private async void RemoveTransfer(object sender, RoutedEventArgs e) => await TransferAction(TransfersGrid, "remove");
    private void OpenMineFolder(object sender, RoutedEventArgs e) => OpenFolder(MineGrid);
    private void OpenTransferFolder(object sender, RoutedEventArgs e) => OpenFolder(TransfersGrid);
    private static void OpenFolder(DataGrid grid)
    {
        if (grid.SelectedItem is TransferRow row && Directory.Exists(row.Snapshot.Directory)) Process.Start(new ProcessStartInfo("explorer.exe", row.Snapshot.Directory) { UseShellExecute = true });
    }
    private async void OpenSettings(object sender, RoutedEventArgs e)
    {
        if (node is null || busy) return;
        var dialog = new SettingsWindow(node.Settings) { Owner = this };
        if (dialog.ShowDialog() != true) return;
        await RunAsync("Применение настроек и восстановление передач…", async () =>
        {
            var old = node.Settings; await node.DisposeAsync();
            dialog.Result.KnownPeers = old.KnownPeers; dialog.Result.Shares = old.Shares;
            await store.SaveAsync(dialog.Result); node = MakeNode(dialog.Result); await node.StartAsync();
        });
        updates = new(dialog.Result.UpdateRepository);
    }
    private async void CheckUpdates(object sender, RoutedEventArgs e) => await CheckUpdatesAsync(true);
    private async Task CheckUpdatesAsync(bool manual)
    {
        if (busy || node is null || updates is null) return;
        if (updates.Ready)
        {
            if (!manual) return;
            if (System.Windows.MessageBox.Show(this, "Сохранить передачи, установить обновление и перезапустить PeerFerry?", "Обновление PeerFerry", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            await RunAsync("Сохранение передач перед обновлением…", async () => { await node.DisposeAsync(); node = null; });
            updates.ApplyAndRestart(); return;
        }
        try
        {
            var message = await Task.Run(() => updates.CheckAndDownloadAsync(progress => Dispatcher.BeginInvoke(() => StatusLabel.Text = $"Загрузка обновления: {progress}%")));
            Log(message); if (manual) System.Windows.MessageBox.Show(this, message, "Обновления PeerFerry");
        }
        catch (Exception ex) { Log("Обновления пока недоступны: " + ex.Message); if (manual) System.Windows.MessageBox.Show(this, "Пока нет доступного релиза или GitHub недоступен.\n" + ex.Message, "Обновления PeerFerry"); }
    }
    private async void OnClosing(object? sender, CancelEventArgs e)
    {
        if (readyToClose) return;
        e.Cancel = true;
        if (!forceExit && node?.Settings.MinimizeToTray == true && App.SmokeDirectory is null) { Hide(); return; }
        if (closing) return;
        closing = true; IsEnabled = false; timer.Stop(); await lifetime.CancelAsync();
        if (pendingOperation is not null) { try { await pendingOperation; } catch (Exception) { } }
        try { if (node is not null) await Task.Run(async () => await node.DisposeAsync()); }
        catch (Exception ex) { File.AppendAllText(Path.Combine(App.DataDirectory, "errors.log"), ex + Environment.NewLine); }
        tray.Visible = false; tray.Dispose(); readyToClose = true; Close();
    }
    private async Task SmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory);
        var sample = Path.Combine(App.DataDirectory, "Пример передачи.txt"); await File.WriteAllTextAsync(sample, "PeerFerry: проверка интерфейса и публикации.");
        await Task.Run(() => node!.Torrents.PublishAsync(sample)); await Task.Delay(350); RenderState();
        if (MineGrid.Items.Count != 1) throw new InvalidOperationException("UI smoke: раздача не появилась.");
        _ = Invitations.Decode(node!.CreateInvitation());
        RenderWindow(this, Path.Combine(directory, "main.png"));
        Tabs.SelectedIndex = 2; UpdateLayout(); RenderWindow(this, Path.Combine(directory, "transfers.png"));
        var settings = new SettingsWindow(node.Settings) { Owner = this, ShowInTaskbar = false }; settings.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); settings.UpdateLayout(); RenderWindow(settings, Path.Combine(directory, "settings.png")); settings.Close();
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: запуск WPF, публикация файла, создание приглашения, отрисовка главного окна, передач и настроек.");
        forceExit = true; Close();
    }
    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth); var height = (int)Math.Ceiling(content.ActualHeight);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height)); drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height)); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
