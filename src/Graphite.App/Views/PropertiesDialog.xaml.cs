using System.Windows;
using Graphite.App.Interop;
using Graphite.App.Services;
using Graphite.Core.Pdf;

namespace Graphite.App.Views;

public partial class PropertiesDialog : Window
{
    public PropertiesDialog()
    {
        InitializeComponent();
        Loaded += (_, _) => Backdrop.Apply(this, ThemeService.IsDark);
    }

    public static void Show(Window owner, string fileName, DocumentProperties p)
    {
        var dlg = new PropertiesDialog { Owner = owner };
        dlg.FileName.Text = fileName;
        dlg.Rows.ItemsSource = new[]
        {
            Tuple.Create("Title", p.Title),
            Tuple.Create("Author", p.Author),
            Tuple.Create("Subject", p.Subject),
            Tuple.Create("Keywords", p.Keywords),
            Tuple.Create("Created", p.Created),
            Tuple.Create("Modified", p.Modified),
            Tuple.Create("Creator", p.Creator),
            Tuple.Create("Producer", p.Producer),
            Tuple.Create("Version", p.Version),
            Tuple.Create("Pages", p.PageCount.ToString()),
            Tuple.Create("Page size", p.PageSize),
            Tuple.Create("File size", p.FileSize),
        };
        dlg.ShowDialog();
    }
}
