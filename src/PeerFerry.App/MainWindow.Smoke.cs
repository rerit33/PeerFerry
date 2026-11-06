using System.Net;
using System.Net.Sockets;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PeerFerry.Core;

namespace PeerFerry.App;

public partial class MainWindow
{
    private async Task SmokeAsync(string directory)
    {
        Directory.CreateDirectory(directory); timer.Stop();
        RenderWindow(this, Path.Combine(directory, "empty.png"));
        var sample = Path.Combine(App.DataDirectory, "Пример передачи.txt"); await File.WriteAllTextAsync(sample, "PeerFerry: проверка интерфейса и публикации.");
        await Task.Run(() => node!.Torrents.PublishAsync(sample)); await Task.Delay(350); RenderState();
        if (transfers.Count != 1) throw new InvalidOperationException("UI smoke: раздача не появилась.");
        _ = Invitations.Decode(node!.CreateInvitation());
        await using var friend = SmokePeer("Андрей"); await using var friend2 = SmokePeer("Михаил");
        await friend.StartAsync(); await friend2.StartAsync();
        var folder = Path.Combine(App.DataDirectory, "Архив с модами"); Directory.CreateDirectory(Path.Combine(folder, "Документы"));
        await File.WriteAllTextAsync(Path.Combine(folder, "Документы", "Инструкция.txt"), "Тестовая передача PeerFerry");
        using (var file = File.Create(Path.Combine(folder, "Модификации.bin"))) file.SetLength(32 * 1024 * 1024);
        var hash = await Task.Run(() => friend.Torrents.PublishAsync(folder)); await Task.Run(() => friend2.Torrents.PublishAsync(folder));
        foreach (var name in new[] { "Запись игры.mp4", "Фотографии.zip", "Сборка сервера.zip" })
        { var path = Path.Combine(App.DataDirectory, name); using (var file = File.Create(path)) file.SetLength(4 * 1024 * 1024 + name.Length); await Task.Run(() => friend.Torrents.PublishAsync(path)); }
        await node.RememberPeerAsync(friend.Descriptor); await node.RememberPeerAsync(friend2.Descriptor); await node.RefreshAsync(); RenderState();
        CatalogGrid.SelectedItem = catalog.First(r => r.Hash == hash); await LoadMetadataAsync();
        var deadline = DateTime.UtcNow.AddSeconds(20); while (selectedMetadata is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (CatalogFiles.Items.Count != 2 || transfers.Count != 1) throw new InvalidOperationException("UI smoke: предпросмотр файлов запустил загрузку или не получил содержимое.");
        OwnerFilter.SelectedValue = friend2.Id; if (CatalogGrid.Items.Count != 1) throw new InvalidOperationException("UI smoke: фильтр участников не работает."); OwnerFilter.SelectedValue = "";
        await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(this, Path.Combine(directory, "available-files.png"));
        CatalogDetails.SelectedIndex = 1; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(this, Path.Combine(directory, "available-participants.png"));
        var entries = node.Catalog();
        await node.DownloadAsync(entries.First(r => r.Entry.Hash == hash), lifetime.Token, selectedMetadata);
        var paused = entries.First(r => r.Entry.Name == "Фотографии.zip"); await node.DownloadAsync(paused); await node.Torrents.PauseAsync(paused.Entry.Hash);
        await node.DownloadAsync(entries.First(r => r.Entry.Name == "Сборка сервера.zip"));
        await Task.Delay(2000); RenderState(); Tabs.SelectedIndex = 1; TransfersGrid.SelectedItem = transfers.First(r => r.Hash == hash); TransferDetails.SelectedIndex = 1;
        await RefreshParticipantsAsync(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(this, Path.Combine(directory, "transfers.png"));
        var selected = TransfersGrid.SelectedItem; RenderState(); if (!ReferenceEquals(selected, TransfersGrid.SelectedItem)) throw new InvalidOperationException("UI smoke: сброшен выбор строки.");
        TransferFilterChanged(new RadioButton { Tag = "Paused" }, new()); if (TransfersGrid.Items.Count != 1) throw new InvalidOperationException("UI smoke: фильтр приостановленных передач не работает.");
        TransferFilterChanged(new RadioButton { Tag = "Seeding" }, new()); if (TransfersGrid.Items.Count != 1) throw new InvalidOperationException("UI smoke: фильтр раздач не работает."); TransferFilterChanged(FilterAll, new());
        TransferSearch.Text = "не существует"; if (TransfersGrid.Items.Count != 0) throw new InvalidOperationException("UI smoke: поиск не фильтрует."); TransferSearch.Clear();
        ToggleDetails(this, new()); if (DetailBody.Visibility != Visibility.Collapsed) throw new InvalidOperationException("UI smoke: панель не сворачивается."); ToggleDetails(this, new());
        TransfersGrid.SelectedItem = selected; Width = 1000; Height = 680; await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(this, Path.Combine(directory, "compact.png")); Width = 1200; Height = 850;
        var settings = new SettingsWindow(node.Settings, () => Task.CompletedTask) { Owner = this, ShowInTaskbar = false }; settings.Show();
        for (var index = 0; index < 3; index++) { settings.SelectSection(index); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(settings, Path.Combine(directory, $"settings-{index}.png")); } settings.Close();
        VerifySettingsEditing();
        var link = new LinkWindow("Пригласить в PeerFerry", "Отправьте приглашение другу в вашей LAN/VPN-сети.", node.CreateInvitation(), true) { Owner = this, ShowInTaskbar = false }; link.Show(); await Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle); RenderWindow(link, Path.Combine(directory, "invitation.png")); link.Close();
        var publication = transfers.Single(t => t.Snapshot.IsPublication);
        await ToggleTransferAsync(publication);
        if (!publication.CanResume) throw new InvalidOperationException("UI smoke: кнопка паузы не остановила раздачу.");
        await ToggleTransferAsync(publication);
        if (publication.CanResume) throw new InvalidOperationException("UI smoke: кнопка продолжения не запустила раздачу.");
        File.WriteAllText(Path.Combine(directory, "result.txt"), "PASS: запуск WPF, публикация, приглашение, предпросмотр до загрузки, загрузка с двух узлов, очередь, пауза, сохранение выбора, поиск, сворачивание, компактное окно и три раздела настроек."); forceExit = true; Close();
    }
    private void VerifySettingsEditing()
    {
        var original = System.Text.Json.JsonSerializer.Serialize(node!.Settings, StateStore.Json);
        var settings = new SettingsWindow(node.Settings, () => Task.CompletedTask) { Owner = this, ShowInTaskbar = false };
        Exception? failure = null;
        settings.Loaded += (_, _) => Dispatcher.BeginInvoke(() =>
        {
            try
            {
                T Field<T>(string name) => (T)settings.FindName(name);
                Field<RadioButton>("ManualMode").IsChecked = true;
                var adapter = Field<DataGrid>("AdaptersGrid").Items.Cast<SettingsWindow.AdapterRow>().FirstOrDefault();
                if (adapter is not null) adapter.Selected = true;
                Field<RadioButton>("AllMode").IsChecked = true; Field<RadioButton>("AutoMode").IsChecked = true; Field<RadioButton>("ManualMode").IsChecked = true;
                if (adapter is not null && !adapter.Selected) throw new InvalidOperationException("Ручной выбор адаптера потерян.");
                Field<CheckBox>("DownloadLimitEnabled").IsChecked = false; Field<TextBox>("DownloadLimitBox").Text = "не число";
                if (Field<TextBox>("DownloadLimitBox").IsEnabled) throw new InvalidOperationException("Отключённый лимит остаётся доступен.");
                Field<CheckBox>("UploadLimitEnabled").IsChecked = true; Field<TextBox>("UploadLimitBox").Text = "2048";
                Field<TextBox>("NameBox").Text = "";
                settings.SaveForSmoke();
                if (string.IsNullOrEmpty(Field<TextBlock>("ErrorLabel").Text)) throw new InvalidOperationException("Пустое имя принято.");
                Field<TextBox>("NameBox").Text = "Тест настроек"; Field<TextBox>("DownloadBox").Text = Path.Combine(App.DataDirectory, "new-downloads"); settings.SaveForSmoke();
            }
            catch (Exception ex) { failure = ex; settings.Close(); }
        }, DispatcherPriority.ApplicationIdle);
        if (settings.ShowDialog() != true || failure is not null) throw new InvalidOperationException("UI smoke: проверка настроек.", failure);
        if (settings.Result.DownloadLimitKiB != 0 || settings.Result.UploadLimitKiB != 2048 || settings.Result.DisplayName != "Тест настроек") throw new InvalidOperationException("UI smoke: неверно сохранены настройки.");
        if (System.Text.Json.JsonSerializer.Serialize(node.Settings, StateStore.Json) != original) throw new InvalidOperationException("UI smoke: диалог изменил настройки до применения.");
    }
    private static int FreeTestPort() { var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var port = ((IPEndPoint)listener.LocalEndpoint).Port; listener.Stop(); return port; }
    private static IReadOnlyList<NetworkBinding> TestBindings() => [new("test", "Локальная тестовая сеть", "", IPAddress.Loopback, 8, 0, false)];
    private MeshNode SmokePeer(string name) => new(new StateStore(Path.Combine(App.DataDirectory, name)), new AppSettings { DisplayName = name, GroupKey = node!.Settings.GroupKey, Port = FreeTestPort(), DownloadDirectory = Path.Combine(App.DataDirectory, name, "downloads"), AutoCheckUpdates = false }, TestBindings, false);
    private static void RenderWindow(Window window, string path)
    {
        window.UpdateLayout(); var content = (FrameworkElement)window.Content;
        var width = (int)Math.Ceiling(content.ActualWidth); var height = (int)Math.Ceiling(content.ActualHeight); var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) { drawing.DrawRectangle(window.Background, null, new Rect(0, 0, width, height)); drawing.DrawRectangle(new VisualBrush(content) { ViewboxUnits = BrushMappingMode.Absolute, Viewbox = new Rect(0, 0, width, height), Stretch = Stretch.Fill }, null, new Rect(0, 0, width, height)); }
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap)); using var stream = File.Create(path); encoder.Save(stream);
    }
}
