using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Export;
using Graphite.Core.Pdf;
using Microsoft.Win32;

namespace Graphite.App.ViewModels;

public sealed record PaletteCommand(string Name, string? Gesture, Action Execute);

public partial class MainViewModel : ObservableObject
{
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();
    public ObservableCollection<string> RecentFiles { get; } = new();

    [ObservableProperty] private DocumentViewModel? selectedDocument;
    [ObservableProperty] private bool isDarkTheme;
    [ObservableProperty] private bool showSidebar = true;
    [ObservableProperty] private bool showInspector = true;
    [ObservableProperty] private bool isFullscreen;

    // Command palette (Ctrl+K).
    [ObservableProperty] private bool isPaletteOpen;
    [ObservableProperty] private string paletteQuery = "";
    [ObservableProperty] private int paletteSelectedIndex;
    public ObservableCollection<PaletteCommand> PaletteResults { get; } = new();
    private List<PaletteCommand> _paletteCommands = new();

    public MainViewModel()
    {
        IsDarkTheme = ThemeService.IsDark;
        foreach (var f in ThemeService.RecentFiles) RecentFiles.Add(f);
    }

    private static Window? Owner => Application.Current.MainWindow;

    private static void Error(Exception ex) =>
        MessageDialog.Show(Owner, ex.Message, "Graphite", DialogButtons.OK, DialogIcon.Warning);

    // ------------------------------------------------------------- open / close

    [RelayCommand]
    private async Task Open()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "All supported|*.pdf;*.doc;*.docx;*.rtf;*.xls;*.xlsx;*.ppt;*.pptx|" +
                     "PDF documents|*.pdf|Office documents|*.doc;*.docx;*.rtf;*.xls;*.xlsx;*.ppt;*.pptx",
        };
        if (dlg.ShowDialog(Owner) == true)
            await OpenFilesAsync(dlg.FileNames);
    }

    [RelayCommand]
    private async Task OpenOneDrive()
    {
        string? oneDrive = Environment.GetEnvironmentVariable("OneDrive")
                        ?? Environment.GetEnvironmentVariable("OneDriveConsumer");
        if (oneDrive == null || !Directory.Exists(oneDrive))
        {
            MessageDialog.Show(Owner, "No OneDrive folder was found on this PC. Sign in to OneDrive first.",
                "Graphite", DialogButtons.OK, DialogIcon.Info);
            return;
        }
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            InitialDirectory = oneDrive,
            Filter = "PDF documents|*.pdf",
        };
        if (dlg.ShowDialog(Owner) == true)
            await OpenFilesAsync(dlg.FileNames);
    }

    [RelayCommand]
    private Task OpenRecent(string path) => OpenFilesAsync(new[] { path });

    public async Task OpenFilesAsync(IEnumerable<string> paths)
    {
        foreach (string path in paths)
        {
            try
            {
                // Already open? Just focus it.
                var existing = Documents.FirstOrDefault(d =>
                    string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { SelectedDocument = existing; continue; }

                DocumentViewModel doc;
                if (OfficeToPdf.CanConvert(path))
                {
                    string temp = Path.Combine(Path.GetTempPath(), $"graphite-{Guid.NewGuid():N}.pdf");
                    await Task.Run(() => OfficeToPdf.Convert(path, temp));
                    doc = await DocumentViewModel.FromBytesAsync(await File.ReadAllBytesAsync(temp), null);
                    try { File.Delete(temp); } catch { }
                }
                else
                {
                    byte[] bytes = await File.ReadAllBytesAsync(path);

                    // Password-protected? Ask, decrypt in memory, and continue normally.
                    if (Graphite.Core.Pdf.PdfSecurity.IsPasswordProtected(bytes))
                    {
                        byte[]? decrypted = PromptAndDecrypt(bytes, Path.GetFileName(path));
                        if (decrypted == null) continue; // user cancelled
                        bytes = decrypted;
                    }

                    doc = await DocumentViewModel.FromBytesAsync(bytes, path);
                    ThemeService.AddRecentFile(path);
                    RecentFiles.Remove(path);
                    RecentFiles.Insert(0, path);
                }

                Documents.Add(doc);
                SelectedDocument = doc;
            }
            catch (Exception ex) { Error(ex); }
        }
    }

    private static byte[]? PromptAndDecrypt(byte[] bytes, string fileName)
    {
        string? error = null;
        while (true)
        {
            string? password = PasswordDialog.Show(Owner!, "Password required",
                $"“{fileName}” is password-protected. Enter the password to open it:", error);
            if (password == null) return null;
            try { return Graphite.Core.Pdf.PdfSecurity.Decrypt(bytes, password); }
            catch { error = "That password didn't work — try again."; }
        }
    }

    [RelayCommand]
    private void CloseDocument(DocumentViewModel? doc)
    {
        if (doc == null) return;
        if (doc.IsDirty)
        {
            var answer = MessageDialog.Show(Owner,
                $"Save changes to \"{doc.Title}\" before closing?",
                "Graphite", DialogButtons.YesNoCancel, DialogIcon.Warning);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes) { _ = SaveDocAsync(doc); }
        }
        Documents.Remove(doc);
        doc.Index.Dispose();
    }

    // ------------------------------------------------------------- save

    [RelayCommand]
    private Task Save() => SelectedDocument == null ? Task.CompletedTask : SaveDocAsync(SelectedDocument);

    private async Task SaveDocAsync(DocumentViewModel doc)
    {
        try
        {
            if (doc.FilePath == null) { await SaveAs(); return; }
            await doc.SaveAsync();
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task SaveAs()
    {
        if (SelectedDocument is not { } doc) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF document|*.pdf",
            FileName = doc.Title,
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try { await doc.SaveAsync(dlg.FileName); ThemeService.AddRecentFile(dlg.FileName); }
        catch (Exception ex) { Error(ex); }
    }

    // ------------------------------------------------------------- page surgery

    [RelayCommand]
    private async Task Merge()
    {
        var dlg = new OpenFileDialog
        {
            Multiselect = true,
            Filter = "PDF documents|*.pdf",
            Title = "Choose PDFs to merge (in selection order)",
        };
        if (dlg.ShowDialog(Owner) != true || dlg.FileNames.Length == 0) return;
        try
        {
            byte[] merged = await Task.Run(() => PageOperations.MergeFiles(dlg.FileNames));
            var doc = await DocumentViewModel.FromBytesAsync(merged, null);
            doc.IsDirty = true;
            Documents.Add(doc);
            SelectedDocument = doc;
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task Split()
    {
        if (SelectedDocument is not { } doc) return;
        var dlg = new OpenFolderDialog { Title = "Choose a folder for the split pages" };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            string baseName = Path.GetFileNameWithoutExtension(doc.Title);
            byte[] bytes = doc.GetBytesWithAnnotations();
            var parts = await Task.Run(() => PageOperations.SplitEachPage(bytes));
            for (int i = 0; i < parts.Count; i++)
                await File.WriteAllBytesAsync(Path.Combine(dlg.FolderName, $"{baseName}-page-{i + 1}.pdf"), parts[i]);
            MessageDialog.Show(Owner, $"Wrote {parts.Count} files to {dlg.FolderName}.", "Split complete",
                DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task DeletePages()
    {
        if (SelectedDocument is not { } doc) return;
        string? ranges = InputDialog.Show(Owner!, "Delete pages", "Pages to delete (e.g. 2, 5-7):",
            (doc.CurrentPageIndex + 1).ToString());
        if (ranges == null) return;
        try
        {
            var pages = PageOperations.ParsePageRanges(ranges, doc.Pages.Count);
            if (pages.Count >= doc.Pages.Count) throw new InvalidOperationException("Cannot delete every page.");
            await doc.ApplyOperationAsync("Deleting pages…", b => PageOperations.DeletePages(b, pages));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task ExtractPages()
    {
        if (SelectedDocument is not { } doc) return;
        string? ranges = InputDialog.Show(Owner!, "Extract pages", "Pages to extract (e.g. 1, 3-5):",
            (doc.CurrentPageIndex + 1).ToString());
        if (ranges == null) return;
        var dlg = new SaveFileDialog { Filter = "PDF document|*.pdf", FileName = "extracted.pdf" };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            var pages = PageOperations.ParsePageRanges(ranges, doc.Pages.Count);
            byte[] bytes = doc.GetBytesWithAnnotations();
            byte[] extracted = await Task.Run(() => PageOperations.ExtractPages(bytes, pages));
            await File.WriteAllBytesAsync(dlg.FileName, extracted);
            await OpenFilesAsync(new[] { dlg.FileName });
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task InsertPages()
    {
        if (SelectedDocument is not { } doc) return;
        var dlg = new OpenFileDialog { Filter = "PDF documents|*.pdf", Title = "Insert pages from…" };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            byte[] other = await File.ReadAllBytesAsync(dlg.FileName);
            int at = doc.CurrentPageIndex + 1;
            await doc.ApplyOperationAsync("Inserting pages…", b => PageOperations.InsertPdf(b, at, other));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task InsertBlankPage()
    {
        if (SelectedDocument is not { } doc) return;
        try
        {
            int at = doc.CurrentPageIndex + 1;
            var (w, h) = doc.Renderer.PageSizes[doc.CurrentPageIndex];
            await doc.ApplyOperationAsync("Inserting page…", b => PageOperations.InsertBlankPage(b, at, w, h));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private Task RotatePageLeft(PageViewModel? page) => RotateAsync(page, -90);

    [RelayCommand]
    private Task RotatePageRight(PageViewModel? page) => RotateAsync(page, 90);

    private async Task RotateAsync(PageViewModel? page, int delta)
    {
        if (SelectedDocument is not { } doc) return;
        int index = page?.Index ?? doc.CurrentPageIndex;
        try { await doc.ApplyOperationAsync("Rotating…", b => PageOperations.RotatePage(b, index, delta)); }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task MovePageUp(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null || page.Index == 0) return;
        try { await doc.ApplyOperationAsync("Reordering…", b => PageOperations.MovePage(b, page.Index, page.Index - 1)); }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task MovePageDown(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null || page.Index >= doc.Pages.Count - 1) return;
        try { await doc.ApplyOperationAsync("Reordering…", b => PageOperations.MovePage(b, page.Index, page.Index + 1)); }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task DeletePage(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null) return;
        if (doc.Pages.Count <= 1) return;
        try { await doc.ApplyOperationAsync("Deleting page…", b => PageOperations.DeletePages(b, new[] { page.Index })); }
        catch (Exception ex) { Error(ex); }
    }

    // ------------------------------------------------------------- export

    [RelayCommand]
    private async Task ExportImages()
    {
        if (SelectedDocument is not { } doc) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PNG image|*.png|JPEG image|*.jpg|WebP image|*.webp",
            FileName = Path.GetFileNameWithoutExtension(doc.Title),
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            doc.IsBusy = true; doc.BusyText = "Exporting images…";
            string ext = Path.GetExtension(dlg.FileName).TrimStart('.');
            string basePath = Path.Combine(Path.GetDirectoryName(dlg.FileName)!,
                Path.GetFileNameWithoutExtension(dlg.FileName));
            var all = Enumerable.Range(0, doc.Pages.Count).ToList();
            var files = await Task.Run(() => ImageExporter.Export(doc.Renderer, all, basePath, ext));
            MessageDialog.Show(Owner, $"Exported {files.Count} image(s).", "Export complete",
                DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
        finally { doc.IsBusy = false; }
    }

    [RelayCommand] private Task ExportWord() => ExportOffice("Word document|*.docx", "Exporting to Word…",
        (doc, path) => WordExporter.Export(doc.Index, path));

    [RelayCommand] private Task ExportExcel() => ExportOffice("Excel workbook|*.xlsx", "Exporting to Excel…",
        (doc, path) => ExcelExporter.Export(doc.Index, path));

    [RelayCommand] private Task ExportPowerPoint() => ExportOffice("PowerPoint presentation|*.pptx", "Exporting to PowerPoint…",
        (doc, path) => PowerPointExporter.Export(doc.Renderer, path));

    private async Task ExportOffice(string filter, string busy, Action<DocumentViewModel, string> export)
    {
        if (SelectedDocument is not { } doc) return;
        var dlg = new SaveFileDialog
        {
            Filter = filter,
            FileName = Path.GetFileNameWithoutExtension(doc.Title),
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            doc.IsBusy = true; doc.BusyText = busy;
            await Task.Run(() => export(doc, dlg.FileName));
            MessageDialog.Show(Owner, $"Exported to {dlg.FileName}.", "Export complete",
                DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
        finally { doc.IsBusy = false; }
    }

    // ------------------------------------------------------------- OCR

    [RelayCommand]
    private async Task RunOcr()
    {
        if (SelectedDocument is not { } doc) return;
        try
        {
            int pages = await doc.RunOcrAsync();
            MessageDialog.Show(Owner,
                pages == 0
                    ? "Every page already has selectable text — nothing to OCR."
                    : $"Recognized text on {pages} page(s). The document is now searchable; save to keep the text layer.",
                "OCR", DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
    }

    // ------------------------------------------------------------- view

    [RelayCommand]
    private void ToggleTheme()
    {
        IsDarkTheme = !IsDarkTheme;
        ThemeService.ApplyTheme(IsDarkTheme);
    }

    [RelayCommand]
    private void SetToolColor(string hex)
    {
        if (SelectedDocument is { } d) d.ActiveColorHex = hex;
    }

    [RelayCommand]
    private void SetToolWidth(string width)
    {
        if (SelectedDocument is { } d &&
            double.TryParse(width, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out double w))
            d.ActiveStrokeWidth = w;
    }

    /// <summary>"text" = drag over words to snap-highlight, "freehand" = draw a translucent marker stroke.</summary>
    [RelayCommand]
    private void SetHighlightMode(string mode)
    {
        if (SelectedDocument is not { } d) return;
        d.HighlightFreehand = mode == "freehand";
        d.ActiveTool = ToolKind.Highlight;
    }

    [RelayCommand] private void ZoomIn() { if (SelectedDocument is { } d) d.Zoom = Math.Min(6, d.Zoom * 1.2); }
    [RelayCommand] private void ZoomOut() { if (SelectedDocument is { } d) d.Zoom = Math.Max(0.25, d.Zoom / 1.2); }

    [RelayCommand]
    private void GoToPageDialog()
    {
        if (SelectedDocument is not { } doc) return;
        string? input = InputDialog.Show(Owner!, "Go to page", $"Page number (1–{doc.Pages.Count}):",
            (doc.CurrentPageIndex + 1).ToString());
        if (input != null && int.TryParse(input, out int page))
            doc.GoToPage(page - 1);
    }

    [RelayCommand]
    private void SetLayout(string mode)
    {
        if (SelectedDocument is { } d && Enum.TryParse<PageLayout>(mode, out var layout))
            d.Layout = layout;
    }

    [RelayCommand] private void ToggleFullscreen() => IsFullscreen = !IsFullscreen;

    [RelayCommand] private void Undo() { if (SelectedDocument is { } d) _ = d.UndoAsync(); }
    [RelayCommand] private void Redo() { if (SelectedDocument is { } d) _ = d.RedoAsync(); }

    // ------------------------------------------------------------- print

    [RelayCommand]
    private async Task Print()
    {
        if (SelectedDocument is not { } doc) return;
        try
        {
            doc.IsBusy = true; doc.BusyText = "Preparing to print…";
            byte[] baked = await Task.Run(doc.GetBytesWithAnnotations);
            doc.IsBusy = false;
            PrintService.Print(baked, doc.Title);
        }
        catch (Exception ex) { Error(ex); }
        finally { doc.IsBusy = false; }
    }

    // ------------------------------------------------------------- document tools

    [RelayCommand]
    private async Task ShowProperties()
    {
        if (SelectedDocument is not { } doc) return;
        try
        {
            var props = await Task.Run(() =>
                Graphite.Core.Pdf.DocumentProperties.Read(doc.GetBytesWithAnnotations(), doc.FilePath));
            PropertiesDialog.Show(Owner!, doc.Title, props);
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task SaveEncryptedCopy()
    {
        if (SelectedDocument is not { } doc) return;
        string? password = PasswordDialog.Show(Owner!, "Protect with password",
            "Choose a password. It will be required to open the copy:");
        if (string.IsNullOrEmpty(password)) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF document|*.pdf",
            FileName = Path.GetFileNameWithoutExtension(doc.Title) + " (protected)",
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            doc.IsBusy = true; doc.BusyText = "Encrypting…";
            byte[] output = await Task.Run(() =>
                Graphite.Core.Pdf.PdfSecurity.Encrypt(doc.GetBytesWithAnnotations(), password));
            await File.WriteAllBytesAsync(dlg.FileName, output);
            MessageDialog.Show(Owner, $"Encrypted copy saved to {dlg.FileName}.", "Graphite",
                DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
        finally { doc.IsBusy = false; }
    }

    [RelayCommand]
    private void EditSignature() => SignatureDialog.Edit(Owner!);

    // ------------------------------------------------------------- command palette

    [RelayCommand]
    public void OpenPalette()
    {
        _paletteCommands = BuildPaletteCommands();
        PaletteQuery = "";
        FilterPalette();
        IsPaletteOpen = true;
    }

    public void ClosePalette() => IsPaletteOpen = false;

    public void ExecutePaletteSelection()
    {
        if (PaletteSelectedIndex < 0 || PaletteSelectedIndex >= PaletteResults.Count) return;
        var cmd = PaletteResults[PaletteSelectedIndex];
        IsPaletteOpen = false;
        cmd.Execute();
    }

    partial void OnPaletteQueryChanged(string value) => FilterPalette();

    private void FilterPalette()
    {
        string q = PaletteQuery.Trim();
        var matches = string.IsNullOrEmpty(q)
            ? _paletteCommands
            : _paletteCommands
                .Where(c => c.Name.Contains(q, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(c => c.Name.StartsWith(q, StringComparison.OrdinalIgnoreCase))
                .ToList();
        PaletteResults.Clear();
        foreach (var m in matches.Take(12)) PaletteResults.Add(m);
        PaletteSelectedIndex = PaletteResults.Count > 0 ? 0 : -1;
    }

    private List<PaletteCommand> BuildPaletteCommands()
    {
        var list = new List<PaletteCommand>
        {
            new("Open a document…", "Ctrl+O", () => _ = Open()),
            new("Merge PDFs…", null, () => _ = Merge()),
            new("Switch light / dark theme", null, ToggleTheme),
            new("Toggle fullscreen reading", "F11", ToggleFullscreen),
            new("Edit signature…", null, EditSignature),
            new("Toggle sidebar", null, () => ShowSidebar = !ShowSidebar),
            new("Toggle markup panel", null, () => ShowInspector = !ShowInspector),
        };

        if (SelectedDocument is { } doc)
        {
            list.InsertRange(1, new PaletteCommand[]
            {
                new("Save", "Ctrl+S", () => _ = Save()),
                new("Save as…", "Ctrl+Shift+S", () => _ = SaveAs()),
                new("Print…", "Ctrl+P", () => _ = Print()),
                new("Go to page…", "Ctrl+G", GoToPageDialog),
                new("Undo", "Ctrl+Z", () => _ = doc.UndoAsync()),
                new("Redo", "Ctrl+Y", () => _ = doc.RedoAsync()),
                new("Document properties", null, () => _ = ShowProperties()),
                new("Save encrypted copy…", null, () => _ = SaveEncryptedCopy()),
                new("Invert page colors (dark reading)", null, () => doc.InvertPages = !doc.InvertPages),
                new("Layout: continuous scrolling", null, () => doc.Layout = PageLayout.Continuous),
                new("Layout: single page", null, () => doc.Layout = PageLayout.Single),
                new("Layout: two-page spread", null, () => doc.Layout = PageLayout.Spread),
                new("Zoom in", "Ctrl++", ZoomIn),
                new("Zoom out", "Ctrl+-", ZoomOut),
                new("Rotate page left", null, () => _ = RotatePageLeft(null)),
                new("Rotate page right", null, () => _ = RotatePageRight(null)),
                new("Recognize text (OCR)", null, () => _ = RunOcr()),
                new("Split into single pages…", null, () => _ = Split()),
                new("Insert pages from PDF…", null, () => _ = InsertPages()),
                new("Insert blank page", null, () => _ = InsertBlankPage()),
                new("Delete pages…", null, () => _ = DeletePages()),
                new("Extract pages…", null, () => _ = ExtractPages()),
                new("Export to Word (.docx)…", null, () => _ = ExportWord()),
                new("Export to Excel (.xlsx)…", null, () => _ = ExportExcel()),
                new("Export to PowerPoint (.pptx)…", null, () => _ = ExportPowerPoint()),
                new("Export pages as images…", null, () => _ = ExportImages()),
                new("Close document", "Ctrl+W", () => CloseDocument(doc)),
            });
        }

        return list;
    }
}
