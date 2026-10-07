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

    /// <summary>Short page tag for the card header ("p. 3").</summary>
    public string PageShort => $"p. {Model.PageIndex + 1}";

    /// <summary>Which Markup-panel filter chip this annotation belongs to.</summary>
    public MarkupFilter Category => Model.Kind switch
    {
        AnnotationKind.Highlight when !Model.IsFreehand => MarkupFilter.Highlights,
        AnnotationKind.Underline or AnnotationKind.StrikeOut => MarkupFilter.Highlights,
        AnnotationKind.Note or AnnotationKind.FreeText => MarkupFilter.Notes,
        _ => MarkupFilter.Ink,
    };

    /// <summary>A translucent version of the swatch colour, for the quoted-text block.</summary>
    public Brush SwatchSoft
    {
        get
        {
            try
            {
                var c = (Color)ColorConverter.ConvertFromString(Model.ColorHex);
                var b = new SolidColorBrush(Color.FromArgb(0x38, c.R, c.G, c.B));
                b.Freeze();
                return b;
            }
            catch { return Brushes.Transparent; }
        }
    }

    private string? _quote;

    /// <summary>The page text a text-markup annotation covers ("" for everything else), quoted
    /// inside its card so the list can be scanned without jumping to each page.</summary>
    public string Quote
    {
        get
        {
            if (_quote != null) return _quote;
            if (Category != MarkupFilter.Highlights || Model.Quads.Count == 0) return _quote = "";
            try
            {
                var parts = Model.Quads
                    .Select(q => Doc.Index.TextInRect(Model.PageIndex, q).Trim())
                    .Where(t => t.Length > 0);
                var text = string.Join(" ", parts);
                if (text.Length > 280) text = text[..280].TrimEnd() + "…";
                return _quote = text;
            }
            catch { return _quote = ""; }
        }
    }

    public bool HasQuote => Quote.Length > 0;

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
