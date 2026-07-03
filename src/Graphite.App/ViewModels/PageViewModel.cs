using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core;

namespace Graphite.App.ViewModels;

public partial class PageViewModel : ObservableObject
{
    public DocumentViewModel Doc { get; }
    public int Index { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    [ObservableProperty] private ImageSource? image;
    [ObservableProperty] private ImageSource? thumbnail;
    [ObservableProperty] private IReadOnlyList<RectD>? searchRects;
    [ObservableProperty] private IReadOnlyList<RectD>? selectionRects;

    public double DisplayWidth => WidthPt * Doc.Zoom;
    public double DisplayHeight => HeightPt * Doc.Zoom;
    public double ThumbHeight => 140 * HeightPt / Math.Max(WidthPt, 1);
    public string Label => (Index + 1).ToString();

    private double _renderedScale;
    private int _rendering;
    private bool _renderQueued;
    private int _thumbRendering;

    public PageViewModel(DocumentViewModel doc, int index)
    {
        Doc = doc;
        Index = index;
        (WidthPt, HeightPt) = doc.Renderer.PageSizes[index];
    }

    public void OnZoomChanged()
    {
        OnPropertyChanged(nameof(DisplayWidth));
        OnPropertyChanged(nameof(DisplayHeight));
    }

    /// <summary>Render (or re-render) the full-size bitmap if the zoom has drifted.</summary>
    public async Task EnsureRenderedAsync(bool force = false)
    {
        double target = TargetScale();
        if (!force && Image != null && Math.Abs(target - _renderedScale) < 0.01) return;

        if (Interlocked.Exchange(ref _rendering, 1) == 1)
        {
            _renderQueued = true;
            return;
        }
        try
        {
            do
            {
                _renderQueued = false;
                double t = TargetScale();
                var bmp = await Task.Run(() =>
                {
                    var rp = Doc.Renderer.Render(Index, t, withAnnotations: false);
                    var b = BitmapSource.Create(rp.Width, rp.Height, 96, 96,
                        PixelFormats.Pbgra32, null, rp.Bgra, rp.Width * 4);
                    b.Freeze();
                    return b;
                });
                Image = bmp;
                _renderedScale = t;
            } while (_renderQueued);
        }
        catch { /* page may have been removed mid-render */ }
        finally { Interlocked.Exchange(ref _rendering, 0); }
    }

    public async Task EnsureThumbnailAsync()
    {
        if (Thumbnail != null || Interlocked.Exchange(ref _thumbRendering, 1) == 1) return;
        try
        {
            double scale = 140.0 / Math.Max(WidthPt, 1);
            var bmp = await Task.Run(() =>
            {
                var rp = Doc.Renderer.Render(Index, scale, withAnnotations: false);
                var b = BitmapSource.Create(rp.Width, rp.Height, 96, 96,
                    PixelFormats.Pbgra32, null, rp.Bgra, rp.Width * 4);
                b.Freeze();
                return b;
            });
            Thumbnail = bmp;
        }
        catch { /* ignore */ }
        finally { Interlocked.Exchange(ref _thumbRendering, 0); }
    }

    public void InvalidateBitmaps()
    {
        Thumbnail = null;
        _renderedScale = 0;
    }

    /// <summary>Drop the full-size rendered bitmap only (keep the cheap thumbnail).
    /// Called for pages that have scrolled well outside the viewport so a long
    /// document doesn't keep every page's decoded bitmap resident forever.
    /// <see cref="EnsureRenderedAsync"/> transparently re-renders on demand once
    /// the page's container comes back on screen.</summary>
    public void EvictFullImage()
    {
        if (Image == null) return;
        Image = null;
        _renderedScale = 0;
    }

    /// <summary>Render above 1:1 for crispness; clamp so huge zooms don't explode memory.</summary>
    private double TargetScale() => Math.Clamp(Doc.Zoom * 1.5, 0.4, 6.0);
}
