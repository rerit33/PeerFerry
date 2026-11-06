using System.Windows;
using System.Windows.Controls;

namespace PeerFerry.App;

internal sealed class LinkWindow : Window
{
    private readonly TextBox text;
    public string Value => text.Text.Trim();
    public LinkWindow(string title, string description, string initial = "", bool readOnly = false)
    {
        Title = title; Width = 720; Height = 330; WindowStartupLocation = WindowStartupLocation.CenterOwner; ResizeMode = ResizeMode.CanResize;
        var panel = new DockPanel { Margin = new Thickness(22) };
        var label = new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 16) }; DockPanel.SetDock(label, Dock.Top); panel.Children.Add(label);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) }; DockPanel.SetDock(actions, Dock.Bottom);
        var primary = new Button { Content = readOnly ? "Копировать приглашение" : "Присоединиться", IsDefault = true };
        primary.Click += (_, _) => { if (readOnly) { try { System.Windows.Clipboard.SetText(Value); } catch (System.Runtime.InteropServices.COMException) { } } else DialogResult = true; };
        var close = new Button { Content = readOnly ? "Закрыть" : "Отмена", IsCancel = true, Margin = new Thickness(0) };
        actions.Children.Add(primary); actions.Children.Add(close); panel.Children.Add(actions);
        text = new TextBox { Text = initial, IsReadOnly = readOnly, TextWrapping = TextWrapping.Wrap, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(text); Content = panel;
        Loaded += (_, _) => { text.Focus(); text.SelectAll(); };
    }
}
