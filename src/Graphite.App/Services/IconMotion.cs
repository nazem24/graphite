using System.Collections;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using WpfPath = System.Windows.Shapes.Path;

namespace Graphite.App.Services;

/// <summary>
/// Click animations for the app's glyph icons. Every Button / ToggleButton / RadioButton gets
/// one automatically: the icon inside it plays a short motion chosen by *which* icon it is
/// (undo swings back, the pen scribbles, the eraser swipes, "+" turns, …). Icons are matched
/// by the shared geometry resource they use (Icons.xaml), so no per-button markup is needed.
///
/// <see cref="Flash"/> additionally morphs a glyph into another one (draw-off / draw-on),
/// used for the save checkmark.
/// </summary>
public static class IconMotion
{
    private static bool _registered;
    private static Dictionary<Geometry, string>? _keys;

    /// <summary>Hook every ButtonBase.Click in the app (handled or not). Call once at startup.</summary>
    public static void Register()
    {
        if (_registered) return;
        _registered = true;
        EventManager.RegisterClassHandler(typeof(ButtonBase), ButtonBase.ClickEvent,
            new RoutedEventHandler(OnClick), handledEventsToo: true);
    }

    private static void OnClick(object sender, RoutedEventArgs e)
    {
        if (!Motion.Enabled || sender is not ButtonBase button) return;
        try
        {
            if (FirstIcon(button) is not { } path || !Keys().TryGetValue(path.Data, out var key)) return;
            Play(path, key);
        }
        catch (Exception ex)
        {
            // Purely cosmetic — never let a click animation break a click.
            App.LogError("Icon animation failed", ex);
        }
    }

    // ------------------------------------------------------------- icon lookup

    private static Dictionary<Geometry, string> Keys()
    {
        if (_keys != null) return _keys;
        var map = new Dictionary<Geometry, string>();
        Collect(Application.Current.Resources, map);
        return _keys = map;
    }

    private static void Collect(ResourceDictionary dictionary, Dictionary<Geometry, string> map)
    {
        foreach (DictionaryEntry entry in dictionary)
            if (entry.Key is string name && name.StartsWith("Icon.", StringComparison.Ordinal) &&
                entry.Value is Geometry geometry && !map.ContainsKey(geometry))
                map[geometry] = name;
        foreach (var merged in dictionary.MergedDictionaries)
            Collect(merged, map);
    }

    /// <summary>The first icon path inside a button (so "icon + chevron" buttons animate the icon).</summary>
    private static WpfPath? FirstIcon(DependencyObject root)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is WpfPath { Data: { } data } path && Keys().ContainsKey(data)) return path;
            if (FirstIcon(child) is { } nested) return nested;
        }
        return null;
    }

    // ------------------------------------------------------------- recipes

    private static void Play(WpfPath path, string key)
    {
        // The theme glyph already has its own spin; leave it alone.
        if (key == "Icon.Theme") return;

        Point origin = key switch
        {
            "Icon.Sidebar" => new Point(0.0, 0.5),
            "Icon.Inspector" => new Point(1.0, 0.5),
            "Icon.Underline" or "Icon.Strike" => new Point(0.5, 0.5),
            _ => new Point(0.5, 0.5),
        };
        if (Motion.RigOf(path, origin) is not { } rig) return;

        switch (key)
        {
            case "Icon.Undo":
                Swing(rig, -30);
                Slide(rig, -2, 0);
                break;
            case "Icon.Redo":
                Swing(rig, 30);
                Slide(rig, 2, 0);
                break;

            case "Icon.ChevronLeft": Slide(rig, -3, 0); break;
            case "Icon.ChevronRight": Slide(rig, 3, 0); break;
            case "Icon.ChevronUp": Slide(rig, 0, -3); break;
            case "Icon.ChevronDown": Slide(rig, 0, 3); break;

            case "Icon.ZoomIn": Scale(rig, (0, 1), (90, 0.84), (230, 1.18), (350, 1)); break;
            case "Icon.ZoomOut": Scale(rig, (0, 1), (90, 1.14), (230, 0.84), (350, 1)); break;
            case "Icon.FitWidth":
            case "Icon.Spread":
            case "Icon.Underline":
            case "Icon.Strike":
                Stretch(rig);
                break;

            case "Icon.Invert": Spin(rig, 360, 480); break;
            case "Icon.Fullscreen": Scale(rig, (0, 1), (110, 0.76), (270, 1.15), (390, 1)); break;
            case "Icon.Command":
                Spin(rig, 90, 380);
                Scale(rig, (0, 1), (150, 1.25), (380, 1));
                break;
            case "Icon.Add":
                Spin(rig, 90, 340);
                Scale(rig, (0, 1), (120, 0.86), (340, 1));
                break;
            case "Icon.Close":
                Spin(rig, 90, 300);
                Scale(rig, (0, 1), (110, 0.78), (300, 1));
                break;

            case "Icon.Sidebar":
            case "Icon.Inspector":
                Motion.Keys(rig.Scale, ScaleTransform.ScaleXProperty, (0, 1), (110, 0.62), (260, 1.1), (360, 1));
                break;

            case "Icon.Search":
                Motion.Keys(rig.Rotate, RotateTransform.AngleProperty,
                    (0, 0), (90, -16), (190, 12), (290, -5), (380, 0));
                break;
            case "Icon.Note": Swing(rig, 14); break;
            case "Icon.Lasso": Swing(rig, 16); break;
            case "Icon.Outline": Slide(rig, 2.5, 0); break;
            case "Icon.Continuous": Slide(rig, 0, -2.5); break;

            case "Icon.Select": Slide(rig, 2, 2); break;
            case "Icon.TextHighlight":
                Slide(rig, 0, -2.5);
                Swing(rig, -8);
                break;
            case "Icon.Ink":
                Swing(rig, -18);
                Slide(rig, 1.8, -1.8);
                break;
            case "Icon.MarkerFreehand":
                Motion.Keys(rig.Move, TranslateTransform.XProperty, (0, 0), (90, -2.4), (200, 2.4), (310, 0));
                break;
            case "Icon.Eraser":
                Motion.Keys(rig.Move, TranslateTransform.XProperty, (0, 0), (70, -2.8), (150, 2.8), (230, -1.8), (330, 0));
                Swing(rig, 6);
                break;
            case "Icon.Signature":
                Motion.Keys(rig.Move, TranslateTransform.XProperty, (0, 0), (100, 2.2), (210, -1.4), (320, 0));
                Swing(rig, -6);
                break;
            case "Icon.Arrow": Slide(rig, 2.2, -2.2); break;
            case "Icon.EditText": Slide(rig, 0, -2.5); break;

            case "Icon.WinMinimize": Slide(rig, 0, 3); break;
            case "Icon.InsertPage": Slide(rig, 0, -2.5); break;
            case "Icon.Export": Slide(rig, 0, -3); break;
            case "Icon.Download": Slide(rig, 0, 3); break;
            case "Icon.Reply": Slide(rig, -3, 0); break;
            case "Icon.Open": Swing(rig, -9); break;

            default: Pop(rig); break;
        }
    }

    private static void Pop(Motion.Rig rig) => Scale(rig, (0, 1), (80, 0.8), (220, 1.12), (330, 1));

    private static void Scale(Motion.Rig rig, params (double Ms, double Value)[] frames)
    {
        Motion.Keys(rig.Scale, ScaleTransform.ScaleXProperty, frames);
        Motion.Keys(rig.Scale, ScaleTransform.ScaleYProperty, frames);
    }

    private static void Stretch(Motion.Rig rig) =>
        Motion.Keys(rig.Scale, ScaleTransform.ScaleXProperty, (0, 1), (100, 0.68), (240, 1.14), (340, 1));

    private static void Swing(Motion.Rig rig, double degrees) =>
        Motion.Keys(rig.Rotate, RotateTransform.AngleProperty,
            (0, 0), (110, degrees), (260, -degrees * 0.25), (380, 0));

    /// <summary>Full turn (or quarter turn for symmetric glyphs) that lands exactly where it started.</summary>
    private static void Spin(Motion.Rig rig, double degrees, double ms) =>
        Motion.Keys(rig.Rotate, RotateTransform.AngleProperty, (0, 0), (ms, degrees));

    private static void Slide(Motion.Rig rig, double dx, double dy)
    {
        if (dx != 0)
            Motion.Keys(rig.Move, TranslateTransform.XProperty, (0, 0), (100, dx), (290, 0));
        if (dy != 0)
            Motion.Keys(rig.Move, TranslateTransform.YProperty, (0, 0), (100, dy), (290, 0));
    }

    // ------------------------------------------------------------- glyph morph

    // Longest stroke we draw on/off, in multiples of the stroke width (1.4 px): 40 ≈ 56 px.
    private const double DashLength = 40;
    private const double Hidden = DashLength + 1; // a hair past the dash so no round cap peeks out

    private sealed class GlyphState
    {
        public int Run;
        public Geometry? Original;
    }

    private static readonly ConditionalWeakTable<WpfPath, GlyphState> Glyphs = new();

    /// <summary>
    /// Morph a single-stroke icon into <paramref name="glyph"/> (the old shape un-draws, the
    /// new one draws in), hold it, then morph back. Only for icons drawn as one continuous
    /// stroke. Under reduced motion the glyph simply swaps for the hold time.
    /// </summary>
    public static async void Flash(WpfPath path, Geometry glyph, int holdMs = 950)
    {
        var state = Glyphs.GetOrCreateValue(path);
        int run = ++state.Run;
        state.Original ??= path.Data;
        bool animate = Motion.Enabled;
        try
        {
            if (animate)
            {
                await Dash(path, 0, Hidden, 170, Motion.EaseIn);
                if (run != state.Run) return;
            }
            path.Data = glyph;
            if (animate)
            {
                await Dash(path, Hidden, 0, 260, Motion.EaseOut);
                if (run != state.Run) return;
            }

            await Task.Delay(holdMs);
            if (run != state.Run) return;

            if (animate)
            {
                await Dash(path, 0, Hidden, 170, Motion.EaseIn);
                if (run != state.Run) return;
            }
            path.Data = state.Original;
            if (animate)
                await Dash(path, Hidden, 0, 240, Motion.EaseOut);
        }
        catch (Exception ex)
        {
            App.LogError("Glyph morph failed", ex);
        }
        finally
        {
            if (run == state.Run)
            {
                path.BeginAnimation(Shape.StrokeDashOffsetProperty, null);
                path.ClearValue(Shape.StrokeDashOffsetProperty);
                path.ClearValue(Shape.StrokeDashArrayProperty);
                path.ClearValue(Shape.StrokeDashCapProperty);
                if (state.Original != null) path.Data = state.Original;
                state.Original = null;
            }
        }
    }

    private static Task Dash(WpfPath path, double from, double to, double ms, IEasingFunction ease)
    {
        if (path.StrokeDashArray.Count == 0)
        {
            path.StrokeDashArray = new DoubleCollection { DashLength, DashLength };
            path.StrokeDashCap = PenLineCap.Round;
        }
        var done = new TaskCompletionSource();
        var animation = new DoubleAnimation(from, to, Motion.Ms(ms))
        {
            EasingFunction = ease,
            FillBehavior = FillBehavior.HoldEnd,
        };
        animation.Completed += (_, _) => done.TrySetResult();
        path.BeginAnimation(Shape.StrokeDashOffsetProperty, animation);
        return done.Task;
    }
}
