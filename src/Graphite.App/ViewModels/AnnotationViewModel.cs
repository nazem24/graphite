using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.Core.Annotations;

namespace Graphite.App.ViewModels;

public partial class AnnotationViewModel : ObservableObject
{
    public DocumentViewModel Doc { get; }
    public Annotation Model { get; }

    public AnnotationViewModel(DocumentViewModel doc, Annotation model)
    {
        Doc = doc;
        Model = model;
        Replies = new ObservableCollection<AnnotationReply>(model.Replies);
    }

    public AnnotationKind Kind => Model.Kind;
    public int PageIndex => Model.PageIndex;
    public string PageLabel => $"Page {Model.PageIndex + 1}";
    public string Author => Model.Author;
    public string When => Model.Modified.ToString("g");

    public string KindLabel => Model.Kind switch
    {
        AnnotationKind.Highlight => Model.IsFreehand ? "Highlight (freehand)" : "Highlight",
        AnnotationKind.Underline => "Underline",
        AnnotationKind.StrikeOut => "Strikethrough",
        AnnotationKind.Ink => "Drawing",
        AnnotationKind.Square => "Rectangle",
        AnnotationKind.Circle => "Ellipse",
        AnnotationKind.FreeText => "Text",
        AnnotationKind.Line => "Arrow",
        _ => "Note",
    };

    public Brush Swatch
    {
        get
        {
            try
            {
                var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(Model.ColorHex));
                b.Freeze();
                return b;
            }
            catch { return Brushes.Gray; }
        }
    }

    public string Contents
    {
        get => Model.Contents;
        set
        {
            if (Model.Contents == value) return;
            // Typing fires this per keystroke — coalesce so one editing burst is one
            // undo step instead of flooding the undo history with per-character states.
            Doc.PushUndoCoalesced($"contents:{Model.Id}");
            Model.Contents = value;
            Model.Modified = DateTime.Now;
            OnPropertyChanged();
            Doc.NotifyAnnotationChanged();
        }
    }

    public ObservableCollection<AnnotationReply> Replies { get; }
    public bool HasReplies => Replies.Count > 0;

    [ObservableProperty] private string replyDraft = "";

    [RelayCommand]
    private void AddReply()
    {
        if (string.IsNullOrWhiteSpace(ReplyDraft)) return;
        Doc.PushUndo();
        var reply = new AnnotationReply { Author = Environment.UserName, Text = ReplyDraft.Trim() };
        Model.Replies.Add(reply);
        Replies.Add(reply);
        ReplyDraft = "";
        OnPropertyChanged(nameof(HasReplies));
        Doc.NotifyAnnotationChanged();
    }

    [RelayCommand]
    private void Delete() => Doc.RemoveAnnotation(this);

    /// <summary>True while this annotation is the document's selected one (drives the
    /// accent outline on its card in the Markup panel).</summary>
    public bool IsSelected => ReferenceEquals(Doc.SelectedAnnotation, this);

    public void RefreshSelection() => OnPropertyChanged(nameof(IsSelected));

    /// <summary>Bumped every time the card is clicked; the card binds it so each click
    /// replays a short "settle" pulse, even when the annotation was already selected.</summary>
    public int PulseCount { get; private set; }

    [RelayCommand]
    private void GoTo()
    {
        Doc.SelectedImage = null;
        Doc.SelectedAnnotation = this;
        Doc.GoToPage(PageIndex, flash: true);
        PulseCount++;
        OnPropertyChanged(nameof(PulseCount));
    }
}
