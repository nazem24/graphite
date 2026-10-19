using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Graphite.App.Services;

namespace Graphite.App.Views;

/// <summary>The settings page, one section at a time (picked on its left). The settings live in
/// <see cref="ViewModels.SettingsViewModel"/>; this file only plays the page transition, which is
/// the same one the start page uses between folders (see <see cref="Motion"/>): the page slides in
/// from the right while fading in, and on the way out fades and drifts a little to the right.</summary>
public partial class SettingsView : UserControl
{
    private int _gen;   // bumped whenever a transition starts or is cut short

    private ViewModels.SettingsViewModel? _watched;

    public SettingsView()
    {
        InitializeComponent();
        DataContextChanged += (_, e) =>
        {
            if (_watched != null) _watched.SectionShown -= OnSectionShown;
            _watched = e.NewValue as ViewModels.SettingsViewModel;
            if (_watched != null) _watched.SectionShown += OnSectionShown;
        };
    }

    private void OnSectionShown() => SectionScroll.ScrollToTop();

    /// <summary>A password box cannot be bound, so its text is handed over here, and cleared once it is saved
    /// (the token is never shown again).</summary>
    private async void TestCanvas_Click(object sender, RoutedEventArgs e)
    {
        if (_watched == null) return;
        try
        {
            if (await _watched.TestAndSaveCanvasAsync(TokenBox.Password)) TokenBox.Clear();
        }
        catch (Exception ex) { App.LogError("Testing the Canvas token failed", ex); }
    }

    private TranslateTransform Move => (TranslateTransform)PageRoot.RenderTransform;

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
        PageRoot.BeginAnimation(OpacityProperty, null);
        PageRoot.Opacity = 1;
        PageRoot.CacheMode = null;
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
        PageRoot.CacheMode = new BitmapCache();
        Animate(PageRoot, OpacityProperty, 0, 1, Motion.PageInFadeMs, Motion.PageFadeEase);
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

        PageRoot.CacheMode ??= new BitmapCache();
        Animate(PageRoot, OpacityProperty, PageRoot.Opacity, 0, Motion.PageOutMs, Motion.PageOutEase);
        Animate(Move, TranslateTransform.XProperty, Move.X, Motion.PageOutDriftPx, Motion.PageOutMs, Motion.PageOutEase);
        Motion.After(Motion.PageOutMs + 20, () =>
        {
            if (gen != _gen) return;
            Rest();
            done();
        });
    }
}
