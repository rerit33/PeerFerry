using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace PeerFerry.App.Controls;

public sealed class AppIcon : Control
{
    public static readonly DependencyProperty KindProperty = DependencyProperty.Register(nameof(Kind), typeof(string), typeof(AppIcon), new FrameworkPropertyMetadata("File", FrameworkPropertyMetadataOptions.AffectsRender));
    public string Kind { get => (string)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    static AppIcon() { WidthProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(20.0)); HeightProperty.OverrideMetadata(typeof(AppIcon), new FrameworkPropertyMetadata(20.0)); }
    private static readonly Dictionary<string, Geometry> Shapes = new Dictionary<string, string>
    {
        ["Add"]="M12,4 V20 M4,12 H20", ["Down"]="M12,3 V16 M7,11 L12,16 17,11 M4,17 V21 H20 V17", ["Up"]="M12,17 V4 M7,9 L12,4 17,9 M4,17 V21 H20 V17",
        ["People"]="M16,21 V18 Q16,14 9,14 Q2,14 2,18 V21 M9,3 A4,4 0 1 1 8.99,3 M18,4 A3,3 0 0 1 18,10 M18,14 Q22,14 22,18 V21",
        ["Invite"]="M16,21 V18 Q16,14 9,14 Q2,14 2,18 V21 M9,3 A4,4 0 1 1 8.99,3 M20,5 V13 M16,9 H24",
        ["Settings"]="M9,2 H15 L16,5 19,6 22,8 20,12 22,16 19,18 16,19 15,22 H9 L8,19 5,18 2,16 4,12 2,8 5,6 8,5 Z M12,8 A4,4 0 1 1 11.99,8",
        ["Folder"]="M2,6 V20 H22 V7 H12 L9,4 H2 Z", ["File"]="M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20", ["Archive"]="M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20 M10,4 H12 M10,7 H12 M10,10 H12 M10,13 H12 M10,16 H12 M10,19 H12",
        ["Video"]="M5,2 H14 L20,8 V22 H5 Z M14,2 V8 H20 M9,11 L15,15 9,19 Z", ["Pause"]="M8,5 V19 M16,5 V19", ["Play"]="M7,4 L19,12 7,20 Z",
        ["More"]="M4,12 H4.1 M12,12 H12.1 M20,12 H20.1", ["Refresh"]="M20,9 A8,8 0 1 0 20,16 M20,3 V9 H14", ["Search"]="M10,3 A7,7 0 1 1 9.99,3 M15,15 L22,22",
        ["ChevronDown"]="M5,9 L12,16 19,9", ["ChevronUp"]="M5,15 L12,8 19,15", ["Network"]="M8,2 H16 V8 H8 Z M12,8 V13 M4,13 H20 M4,13 V17 M20,13 V17 M1,17 H7 V23 H1 Z M17,17 H23 V23 H17 Z",
        ["Check"]="M4,12 L9,17 20,5", ["Swap"]="M3,7 H21 M16,2 L21,7 16,12 M21,17 H3 M8,12 L3,17 8,22", ["Close"]="M5,5 L19,19 M19,5 L5,19",
        ["Wifi"]="M2,7 Q12,-1 22,7 M6,12 Q12,6 18,12 M10,17 Q12,15 14,17 M12,21 H12.1"
    }.ToDictionary(p => p.Key, p => Geometry.Parse(p.Value));
    protected override void OnRender(DrawingContext dc)
    {
        base.OnRender(dc);
        var pen = new Pen(Foreground, Kind == "More" ? 3.2 : 1.7) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round, LineJoin = PenLineJoin.Round };
        dc.PushTransform(new ScaleTransform(ActualWidth / 24, ActualHeight / 24));
        dc.DrawGeometry(null, pen, Shapes.GetValueOrDefault(Kind, Shapes["File"])); dc.Pop();
    }
}
