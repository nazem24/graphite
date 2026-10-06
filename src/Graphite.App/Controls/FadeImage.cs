using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Graphite.App.Services;

namespace Graphite.App.Controls;

/// <summary>
/// An <see cref="Image"/> that fades in when its bitmap first arrives (null → bitmap), so a
/// page or thumbnail that finishes rendering settles in instead of popping. Re-renders that
/// swap one bitmap for another (zoom, invert) stay instant — a fade there would flash — except
/// for a short window after <see cref="BeginCrossFade"/> (the dark-pages toggle).
/// </summary>
public sealed class FadeImage : Image
{
    private static long _crossFadeUntil;

    /// <summary>Bitmap swaps in the next couple of seconds cross-fade instead of cutting.</summary>
    public static void BeginCrossFade(double seconds = 1.6) =>
        _crossFadeUntil = Stopwatch.GetTimestamp() + (long)(seconds * Stopwatch.Frequency);

    protected override void OnPropertyChanged(DependencyPropertyChangedEventArgs e)
    {
        base.OnPropertyChanged(e);
        if (e.Property != SourceProperty || e.NewValue == null || !Motion.Enabled) return;

        bool first = e.OldValue == null;
        bool crossFade = !first && Stopwatch.GetTimestamp() < _crossFadeUntil;
        if (!first && !crossFade) return;

        Motion.Tween(this, OpacityProperty, first ? 0 : 0.3, 1, first ? 200 : 300);
    }
}
