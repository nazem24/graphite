using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Graphite.App.ViewModels;

/// <summary>
/// The start screen has two spaces, switched at the top of the sidebar: Local (the library folder, unchanged)
/// and Canvas (courses from Canvas, see <see cref="CanvasViewModel"/>). Canvas is created the first time it is
/// opened and does nothing in the background while Local is showing. The app always starts in Local.
/// </summary>
public partial class LibraryViewModel
{
    private CanvasViewModel? _canvas;

    /// <summary>The Canvas space (created on first use).</summary>
    public CanvasViewModel Canvas => _canvas ??= new CanvasViewModel(_main);

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsLocalSpace))]
    private bool isCanvasSpace;

    public bool IsLocalSpace => !IsCanvasSpace;

    /// <summary>Raised before the space changes, so the view can fade the page out first.</summary>
    public event Action? SpaceChanging;

    partial void OnIsCanvasSpaceChanging(bool value) => SpaceChanging?.Invoke();

    partial void OnIsCanvasSpaceChanged(bool value)
    {
        if (value)
        {
            ClearSelection();     // the bar's selection actions belong to Local
            OnPropertyChanged(nameof(Canvas));
            Canvas.Activate();
        }
        else
        {
            _canvas?.Deactivate();
            OnHomeShown();
        }
    }

    [RelayCommand]
    private void ShowLocalSpace() => IsCanvasSpace = false;

    [RelayCommand]
    private void ShowCanvasSpace() => IsCanvasSpace = true;
}
