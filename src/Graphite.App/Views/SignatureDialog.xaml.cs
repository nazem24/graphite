using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Graphite.App.Controls;
using Graphite.App.Interop;
using Graphite.App.Services;

namespace Graphite.App.Views;

public partial class SignatureDialog : Window
{
    private readonly List<List<Point>> _strokes = new();
    private List<Point>? _stroke;
    private Path? _path;

    public SignatureDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
    }

    private void Pad_Down(object sender, MouseButtonEventArgs e)
    {
        _stroke = new List<Point> { e.GetPosition(Pad) };
        _path = new Path
        {
            Stroke = new SolidColorBrush(Color.FromRgb(0x1B, 0x3A, 0x6B)),
            StrokeThickness = 2.2,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Data = StrokeSmoothing.ToSmoothGeometry(_stroke),
        };
        Pad.Children.Add(_path);
        Pad.CaptureMouse();
    }

    private void Pad_Move(object sender, MouseEventArgs e)
    {
        if (_stroke == null || _path == null || e.LeftButton != MouseButtonState.Pressed) return;
        _stroke.Add(e.GetPosition(Pad));
        _path.Data = StrokeSmoothing.ToSmoothGeometry(_stroke);
    }

    private void Pad_Up(object sender, MouseEventArgs e)
    {
        if (_stroke == null) return;
        if (_stroke.Count > 1) _strokes.Add(_stroke);
        else if (_path != null) Pad.Children.Remove(_path);
        _stroke = null;
        _path = null;
        Pad.ReleaseMouseCapture();
        OkButton.IsEnabled = _strokes.Count > 0;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        _strokes.Clear();
        Pad.Children.Clear();
        OkButton.IsEnabled = false;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        // Normalize: x in 0..1, y scaled by the same factor (so y max = aspect ratio).
        double minX = _strokes.SelectMany(s => s).Min(p => p.X);
        double maxX = _strokes.SelectMany(s => s).Max(p => p.X);
        double minY = _strokes.SelectMany(s => s).Min(p => p.Y);
        double w = Math.Max(1, maxX - minX);

        var normalized = _strokes
            .Select(s => s.Select(p => new[] { (p.X - minX) / w, (p.Y - minY) / w }).ToList())
            .ToList();

        ThemeService.SetSignature(normalized);
        DialogResult = true;
    }

    /// <summary>Ensure a signature exists; prompts to draw one if needed. True when available.</summary>
    public static bool EnsureSignature(Window owner)
    {
        if (ThemeService.HasSignature) return true;
        return new SignatureDialog { Owner = owner }.ShowDialog() == true;
    }

    public static void Edit(Window owner) => new SignatureDialog { Owner = owner }.ShowDialog();
}
