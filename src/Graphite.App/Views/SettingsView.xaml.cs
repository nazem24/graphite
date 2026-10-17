using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.Services;

namespace Graphite.App.Views;

/// <summary>The settings page (mode, window material, start page, motion). The settings live in
/// <see cref="ViewModels.SettingsViewModel"/>; this file only plays the page transition, which is
/// the same one the start page uses between folders (see <see cref="Motion"/>): the page slides in
/// from the right while fading in, and on the way out fades and drifts a little to the right.</summary>
public partial class SettingsView : UserControl
{
    private int _gen;   // bumped whenever a transition starts or is cut short

    public SettingsView() => InitializeComponent();

    private TranslateTransform Move => (TranslateTransform)PageScroll.RenderTransform;

    private static DoubleAnimation Animate(IAnimatable target, DependencyProperty property, double from, double to,
        double ms, IEasingFunction ease)
    {
        var anim = new DoubleAnimation(from, to, Motion.Ms(ms)) { EasingFunction = ease, FillBehavior = FillBehavior.HoldEnd };
        target.BeginAnimation(property, anim);
        return anim;
    }

    /// <summary>Everything at rest: fully visible, no animation running, drawn live (not as a cached picture).</summary>
    private void Rest()
    {
        PageScroll.BeginAnimation(OpacityProperty, null);
        PageScroll.Opacity = 1;
        PageScroll.CacheMode = null;
        Move.BeginAnimation(TranslateTransform.XProperty, null);
        Move.X = 0;
    }

    /// <summary>The page was just made visible: slide and fade it in.</summary>
    public void Enter()
    {
        int gen = ++_gen;
        Rest();
        if (!Motion.Enabled) return;

        // one cached picture, so the movement runs on the graphics card alone
        PageScroll.CacheMode = new BitmapCache();
        Animate(PageScroll, OpacityProperty, 0, 1, Motion.PageInFadeMs, Motion.PageFadeEase);
        Animate(Move, TranslateTransform.XProperty, Motion.PageInSlidePx, 0, Motion.PageInSlideMs, Motion.PageSlideEase);
        Motion.After(Motion.PageInSlideMs + 40, () =>
        {
            if (gen == _gen) Rest();
        });
    }

    /// <summary>Fade the page out; <paramref name="done"/> runs once it is invisible (and only if it was
    /// not opened again in the meantime).</summary>
    public void Exit(Action done)
    {
        int gen = ++_gen;
        if (!Motion.Enabled)
        {
            Rest();
            done();
            return;
        }

        PageScroll.CacheMode ??= new BitmapCache();
        Animate(PageScroll, OpacityProperty, PageScroll.Opacity, 0, Motion.PageOutMs, Motion.PageOutEase);
        Animate(Move, TranslateTransform.XProperty, Move.X, Motion.PageOutDriftPx, Motion.PageOutMs, Motion.PageOutEase);
        Motion.After(Motion.PageOutMs + 20, () =>
        {
            if (gen != _gen) return;
            Rest();
            done();
        });
    }
}
