using System.Windows.Threading;
using Graphite.App.ViewModels;

namespace Graphite.App.Views;

/// <summary>
/// Memory policy for background tabs. Every open document keeps its viewer so switching is
/// instant, but the rendered page bitmaps are the expensive part, so they are given back in
/// two steps:
///   1. a tab that has been in the background for <see cref="TrimDelay"/> drops every page
///      bitmap except the ones on screen (switching back is still instant);
///   2. only the <see cref="HotTabs"/> most recently used tabs keep even those; older tabs
///      drop all their bitmaps and re-render the visible pages when they are shown again
///      (still far quicker than the old full rebuild — the viewer and scroll position stay).
///
/// On top of that, <see cref="BitmapBudget"/> caps the total memory of all page bitmaps across
/// every tab, so one huge zoom (or many big tabs) can't run away. When the cap is exceeded the
/// least valuable bitmaps go first: background tabs' off-screen pages, then the active tab's
/// off-screen pages (farthest first), then background tabs' on-screen pages (oldest tab first).
/// The pages you are looking at in the active tab are never touched.
/// </summary>
public partial class MainWindow
{
    /// <summary>Upper bound for all rendered page bitmaps together (~4 bytes per pixel).</summary>
    private const long BitmapBudget = 400L * 1024 * 1024;
    private bool _budgetCheckQueued;

    /// <summary>How many tabs (including the active one) stay fully warm.</summary>
    private const int HotTabs = 3;
    private static readonly TimeSpan TrimDelay = TimeSpan.FromSeconds(15);

    private readonly List<DocumentViewModel> _recentTabs = new(); // most recently used first
    private readonly Dictionary<DocumentViewModel, DateTime> _leftAt = new();
    private readonly Dictionary<DocumentViewModel, int> _trimLevel = new(); // 0 none, 1 off-screen, 2 all
    private DispatcherTimer? _trimTimer;

    private void NoteTabSwitch(DocumentViewModel? prev, DocumentViewModel? next)
    {
        // (a tab that was just closed is already forgotten — don't resurrect it)
        if (prev != null && ViewModel.Documents.Contains(prev)) _leftAt[prev] = DateTime.UtcNow;
        if (next != null)
        {
            _recentTabs.Remove(next);
            _recentTabs.Insert(0, next);
            _leftAt.Remove(next);
            _trimLevel.Remove(next);
        }

        if (_trimTimer == null)
        {
            PageViewModel.BitmapReady += OnBitmapReady;
            _trimTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(5) };
            _trimTimer.Tick += (_, _) => TrimBackgroundTabs();
            _trimTimer.Start();
        }
    }

    private void OnBitmapReady()
    {
        // Renders finish in bursts; check once after the burst rather than per page.
        if (_budgetCheckQueued) return;
        _budgetCheckQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            _budgetCheckQueued = false;
            EnforceBitmapBudget();
        });
    }

    private void EnforceBitmapBudget()
    {
        long total = 0;
        var evictable = new List<(PageViewModel Page, int Tier, int TabAge, int Distance, long Bytes)>();

        foreach (var doc in ViewModel.Documents)
        {
            bool active = ReferenceEquals(doc, ViewModel.SelectedDocument);
            int age = _recentTabs.IndexOf(doc);
            if (age < 0) age = int.MaxValue / 2;

            foreach (var page in doc.Pages)
            {
                long bytes = page.BitmapBytes;
                if (bytes == 0) continue;
                total += bytes;

                bool shown = doc.IsShownPage(page.Index);
                if (active && shown) continue; // what you are looking at is never evicted

                int tier = !active && !shown ? 0 : active ? 1 : 2;
                evictable.Add((page, tier, age, doc.DistanceFromShown(page.Index), bytes));
            }
        }

        if (total <= BitmapBudget) return;

        foreach (var candidate in evictable
                     .OrderBy(c => c.Tier)
                     .ThenByDescending(c => c.TabAge)     // oldest background tab first
                     .ThenByDescending(c => c.Distance))  // farthest from what is shown first
        {
            candidate.Page.EvictFullImage();
            total -= candidate.Bytes;
            if (total <= BitmapBudget) break;
        }
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
    }

    private void ForgetTab(DocumentViewModel doc)
    {
        _recentTabs.Remove(doc);
        _leftAt.Remove(doc);
        _trimLevel.Remove(doc);
    }

    private void TrimBackgroundTabs()
    {
        EnforceBitmapBudget(); // safety net alongside the after-render check
        if (ViewModel.Documents.Count < 2) return;

        bool trimmed = false;
        var now = DateTime.UtcNow;
        for (int rank = 0; rank < _recentTabs.Count; rank++)
        {
            var doc = _recentTabs[rank];
            if (ReferenceEquals(doc, ViewModel.SelectedDocument) || !ViewModel.Documents.Contains(doc)) continue;

            int wanted = rank >= HotTabs ? 2
                : _leftAt.TryGetValue(doc, out var left) && now - left >= TrimDelay ? 1
                : 0;
            if (wanted <= _trimLevel.GetValueOrDefault(doc)) continue;

            doc.TrimHidden(everything: wanted == 2);
            _trimLevel[doc] = wanted;
            trimmed = true;
        }

        // Let the freed bitmaps actually go back to the OS instead of waiting for a later GC.
        if (trimmed) GC.Collect(GC.MaxGeneration, GCCollectionMode.Optimized, blocking: false);
    }
}
