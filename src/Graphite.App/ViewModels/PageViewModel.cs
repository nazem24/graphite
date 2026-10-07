using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using Graphite.Core;
using Graphite.Core.Rendering;

namespace Graphite.App.ViewModels;

public partial class PageViewModel : ObservableObject
{
    public DocumentViewModel Doc { get; }
    public int Index { get; }
    public double WidthPt { get; }
    public double HeightPt { get; }

    [ObservableProperty] private ImageSource? image;
    [ObservableProperty] private IReadOnlyList<RectD>? searchRects;
    [ObservableProperty] private IReadOnlyList<RectD>? selectionRects;

    public double DisplayWidth => WidthPt * Doc.Zoom;
    public double DisplayHeight => HeightPt * Doc.Zoom;
    public double ThumbHeight => 140 * HeightPt / Math.Max(WidthPt, 1);
    public string Label => (Index + 1).ToString();

    /// <summary>Physical pixels per DIP of the monitor the main window is on (1.0 at 100 %
    /// scaling, 1.5 at 150 %, …). Set by the window; pages render to match it.</summary>
    public static double DeviceScale { get; set; } = 1.0;

    /// <summary>Upper bound on a single page bitmap (pixels). 24 MP ≈ 96 MB of BGRA —
    /// beyond this a page can't be told apart on screen anyway, and one huge zoom used
    /// to allocate several hundred MB across the handful of pages kept rendered.</summary>
    private const double MaxPagePixels = 24_000_000;

    private double _renderedScale;

    /// <summary>Approximate memory held by the full-size bitmap (premultiplied BGRA, 4 bytes
    /// per pixel); 0 when the page has none. UI thread only.</summary>
    public long BitmapBytes => Image == null
        ? 0
        : (long)(Math.Max(1, WidthPt * _renderedScale) * Math.Max(1, HeightPt * _renderedScale) * 4);

    /// <summary>Raised on the UI thread whenever a page finished rendering a new bitmap, so
    /// the window can keep the total under its memory budget.</summary>
    public static event Action? BitmapReady;

    private int _rendering;
    private bool _renderQueued;
    private int _thumbRendering;

    // Bumped whenever the full-size bitmap is no longer wanted (page scrolled far away,
    // document reset). Renders still waiting for the PDFium lock check it and bail out,
    // so a fast fling through a long document doesn't leave dozens of stale page renders
    // queued in front of the pages the user actually stopped on.
    private int _generation;

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

    private static BitmapSource ToBitmap(int w, int h, IntPtr pixels, int size, int stride)
    {
        // Copies straight out of PDFium's native buffer into WPF's — no managed byte[] in between.
        var b = BitmapSource.Create(w, h, 96, 96, PixelFormats.Pbgra32, null, pixels, size, stride);
        b.Freeze();
        return b;
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
                int gen = Volatile.Read(ref _generation);

                // A page that is only realized as scroll cache (not on screen yet) waits a
                // beat, so the pages the user is actually looking at get PDFium first. It
                // starts straight away the moment it scrolls into view.
                for (int wait = 0; wait < 6 && !Doc.IsPageOnScreen(Index) &&
                                   Volatile.Read(ref _generation) == gen; wait++)
                    await Task.Delay(40);
                if (Volatile.Read(ref _generation) != gen) continue;

                double t = TargetScale();
                var renderer = Doc.Renderer;
                int index = Index;
                var bmp = await Task.Run(() =>
                    Volatile.Read(ref _generation) != gen
                        ? null // evicted while waiting — skip the work entirely
                        : renderer.Render<BitmapSource>(index, t, false, ToBitmap));
                if (bmp == null || Volatile.Read(ref _generation) != gen) continue;
                Image = bmp;
                _renderedScale = t;
                BitmapReady?.Invoke();
            } while (_renderQueued && (Image == null || Math.Abs(TargetScale() - _renderedScale) >= 0.01));
        }
        catch (Exception ex) { App.LogError($"Page {Index} render failed", ex); }
        finally { Interlocked.Exchange(ref _rendering, 0); }
    }

    // ------------------------------------------------------------- thumbnail

    private ImageSource? _thumbnail;
    private bool _thumbStale = true;

    /// <summary>Sidebar thumbnail. Rendered lazily: reading it (which only happens for
    /// thumbnail containers that are actually realized) kicks off a render when it's
    /// missing or stale. Invalidation therefore only re-renders the handful of visible
    /// thumbnails instead of queueing a render for every page of the document.</summary>
    public ImageSource? Thumbnail
    {
        get
        {
            if (_thumbStale) _ = EnsureThumbnailAsync();
            return _thumbnail;
        }
        private set => SetProperty(ref _thumbnail, value);
    }

    public async Task EnsureThumbnailAsync()
    {
        if (!_thumbStale || Interlocked.Exchange(ref _thumbRendering, 1) == 1) return;
        try
        {
            _thumbStale = false;
            double scale = 140.0 / Math.Max(WidthPt, 1) * Math.Max(1.0, DeviceScale);
            var renderer = Doc.Renderer;
            int index = Index;
            Thumbnail = await Task.Run(() => renderer.Render<BitmapSource>(index, scale, false, ToBitmap));
        }
        catch (Exception ex)
        {
            _thumbStale = true;
            App.LogError($"Page {Index} thumbnail failed", ex);
        }
        finally { Interlocked.Exchange(ref _thumbRendering, 0); }
    }

    /// <summary>Mark both bitmaps out of date (e.g. dark-mode invert toggled). The old
    /// thumbnail stays on screen until its replacement is ready, so the rail doesn't flash.</summary>
    public void InvalidateBitmaps()
    {
        _thumbStale = true;
        _renderedScale = 0;
        OnPropertyChanged(nameof(Thumbnail)); // realized thumbnails re-read → re-render
    }

    /// <summary>Drop the full-size rendered bitmap only (keep the cheap thumbnail).
    /// Called for pages that have scrolled well outside the viewport so a long
    /// document doesn't keep every page's decoded bitmap resident forever.
    /// <see cref="EnsureRenderedAsync"/> transparently re-renders on demand once
    /// the page comes back on screen.</summary>
    public void EvictFullImage()
    {
        Interlocked.Increment(ref _generation); // also cancels a render still in the queue
        if (Image == null) return;
        Image = null;
        _renderedScale = 0;
    }

    /// <summary>Render at the monitor's real pixel density, with a little headroom on
    /// 100 % displays where page edges rarely land on whole pixels. The old fixed 1.5×
    /// oversample spent 2.25× the pixels (memory and CPU) at 100 % scaling and was still
    /// soft at 200 %.</summary>
    private double TargetScale()
    {
        double density = Math.Max(DeviceScale, 1.25);
        double scale = Math.Clamp(Doc.Zoom * density, 0.4, 12.0);
        double area = Math.Max(1, WidthPt * HeightPt);
        double maxScale = Math.Sqrt(MaxPagePixels / area);
        return Math.Min(scale, maxScale);
    }
}
