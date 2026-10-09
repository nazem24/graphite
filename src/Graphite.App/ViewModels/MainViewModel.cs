using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Export;
using Graphite.Core.Library;
using Graphite.Core.Pdf;
using Microsoft.Win32;

namespace Graphite.App.ViewModels;

public sealed record PaletteCommand(string Name, string? Gesture, Action Execute);

public partial class MainViewModel : ObservableObject
{
    /// <summary>The open PDFs.</summary>
    public ObservableCollection<DocumentViewModel> Documents { get; } = new();

    /// <summary>The open documents of the built-in editor.</summary>
    public ObservableCollection<WriterViewModel> Writers { get; } = new();

    /// <summary>Every open tab (PDFs and documents) in the order they were opened: what the tab strip shows.</summary>
    public ObservableCollection<object> Tabs { get; } = new();

    public ObservableCollection<string> RecentFiles { get; } = new();

    /// <summary>The selected PDF; null on the Home tab and while a document is selected.</summary>
    [ObservableProperty] private DocumentViewModel? selectedDocument;

    /// <summary>The selected document of the editor; null on the Home tab and while a PDF is selected.</summary>
    [ObservableProperty] private WriterViewModel? selectedWriter;

    /// <summary>The selected tab, whatever its kind (null = Home). Kept in step with the two above.</summary>
    [ObservableProperty] private object? selectedTab;

    private bool _syncingTabs;
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

    /// <summary>The start screen's folder library (Home tab).</summary>
    public LibraryViewModel Library { get; }

    public MainViewModel()
    {
        IsDarkTheme = ThemeService.IsDark;
        foreach (var f in ThemeService.RecentFiles) RecentFiles.Add(f);
        Documents.CollectionChanged += (_, e) => MirrorTabs(e);
        Writers.CollectionChanged += (_, e) => MirrorTabs(e);
        Library = new LibraryViewModel(this);
        Library.Initialize();
    }

    private void MirrorTabs(NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems != null)
            foreach (object item in e.OldItems) Tabs.Remove(item);
        if (e.NewItems != null)
            foreach (object item in e.NewItems) Tabs.Add(item);
    }

    private static Window? Owner => Application.Current.MainWindow;

    /// <summary>True while the Home tab (start screen) is showing instead of a document.</summary>
    public bool IsHomeSelected => SelectedDocument == null && SelectedWriter == null;

    /// <summary>A PDF is showing (the markup tools apply).</summary>
    public bool IsPdfSelected => SelectedDocument != null;

    /// <summary>A document of the built-in editor is showing.</summary>
    public bool IsWriterSelected => SelectedWriter != null;

    /// <summary>Switch to the Home tab. Open documents stay open in their own tabs.</summary>
    [RelayCommand]
    private void GoHome() => SelectedTab = null;

    private void RaiseSelectionKinds()
    {
        OnPropertyChanged(nameof(IsHomeSelected));
        OnPropertyChanged(nameof(IsPdfSelected));
        OnPropertyChanged(nameof(IsWriterSelected));
    }

    partial void OnSelectedTabChanged(object? value)
    {
        if (_syncingTabs) return;
        _syncingTabs = true;
        try
        {
            SelectedWriter = value as WriterViewModel;
            SelectedDocument = value as DocumentViewModel;
        }
        finally { _syncingTabs = false; }
        RaiseSelectionKinds();
    }

    partial void OnSelectedWriterChanged(WriterViewModel? oldValue, WriterViewModel? newValue)
    {
        if (oldValue != null) oldValue.IsActive = false;
        if (newValue != null) newValue.IsActive = true;
        if (!_syncingTabs)
        {
            _syncingTabs = true;
            try
            {
                if (newValue != null) SelectedDocument = null;
                SelectedTab = newValue;
            }
            finally { _syncingTabs = false; }
        }
        RaiseSelectionKinds();
        if (newValue == null && SelectedDocument == null) Library.OnHomeShown();
    }

    partial void OnSelectedDocumentChanged(DocumentViewModel? oldValue, DocumentViewModel? newValue)
    {
        if (oldValue != null)
        {
            oldValue.IsActive = false;
            SaveReadingPosition(oldValue);
        }
        if (newValue != null) newValue.IsActive = true;
        if (!_syncingTabs)
        {
            _syncingTabs = true;
            try
            {
                SelectedWriter = null;
                SelectedTab = newValue;
            }
            finally { _syncingTabs = false; }
        }
        RaiseSelectionKinds();
        if (newValue == null && SelectedWriter == null) Library.OnHomeShown();
    }

    /// <summary>Remember the page this document was left on, so the start screen can offer
    /// "continue reading" and reopen the file where it stopped.</summary>
    private static void SaveReadingPosition(DocumentViewModel doc)
    {
        if (doc.FilePath is { Length: > 0 } path && doc.Pages.Count > 0)
            ThemeService.SetReading(path, doc.CurrentPageIndex, doc.Pages.Count);
    }

    /// <summary>Save where every open document is (the window is closing).</summary>
    public void SaveAllReadingPositions()
    {
        foreach (var doc in Documents) SaveReadingPosition(doc);
    }

    private static void Error(Exception ex)
    {
        App.LogError("Operation failed", ex);
        MessageDialog.Show(Owner, ex.Message, "Graphite", DialogButtons.OK, DialogIcon.Warning);
    }

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

    /// <param name="activate">False opens the file in a background tab and leaves the current
    /// tab (or the start screen) showing — the library's "open in new tab" button.</param>
    /// <param name="resume">Reopen a PDF on the page the person last left it on.</param>
    public async Task OpenFilesAsync(IEnumerable<string> paths, bool activate = true, bool resume = false)
    {
        foreach (string path in paths)
        {
            try
            {
                // Already open? Just focus it.
                var existing = Documents.FirstOrDefault(d =>
                    string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (existing != null) { if (activate) SelectedTab = existing; continue; }
                var openWriter = Writers.FirstOrDefault(w =>
                    string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase));
                if (openWriter != null) { if (activate) SelectedTab = openWriter; continue; }

                // A document written by Graphite's own editor opens in the editor; any other
                // Word file is converted to PDF with Office as before.
                if (GraphiteDocx.IsGraphiteDocument(path))
                    OpenWriter(path, activate);
                else if (OfficeToPdf.CanConvert(path))
                    await OpenOfficeAsPdfAsync(path);
                else
                    await OpenPdfAsync(path, activate, resume);
            }
            catch (Exception ex) { Error(ex); }
        }
    }

    /// <summary>Convert an Office document to a PDF saved next to the source file
    /// (report.docx → report.pdf), then open that PDF as a normal file-backed document.
    /// When the source folder isn't writable, fall back to a throwaway temp copy.</summary>
    private async Task OpenOfficeAsPdfAsync(string path)
    {
        string target = Path.Combine(
            Path.GetDirectoryName(path) ?? "",
            Path.GetFileNameWithoutExtension(path) + ".pdf");

        if (File.Exists(target))
        {
            var answer = MessageDialog.Show(Owner,
                $"\"{Path.GetFileName(target)}\" already exists next to the document.\n\n" +
                "Yes = convert again and replace it · No = open the existing PDF · Cancel = do nothing.",
                "Convert to PDF", DialogButtons.YesNoCancel, DialogIcon.Info);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.No) { await OpenPdfAsync(target); return; }
        }

        try
        {
            await Task.Run(() => OfficeToPdf.Convert(path, target));
            await OpenPdfAsync(target);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Can't write next to the source (read-only folder, locked file) — fall
            // back to an in-memory temp copy so the document still opens.
            string temp = Path.Combine(Path.GetTempPath(), $"graphite-{Guid.NewGuid():N}.pdf");
            try
            {
                await Task.Run(() => OfficeToPdf.Convert(path, temp));
                var doc = await DocumentViewModel.FromBytesAsync(await File.ReadAllBytesAsync(temp), null);
                Documents.Add(doc);
                SelectedDocument = doc;
                MessageDialog.Show(Owner,
                    $"Couldn't save the PDF next to the document ({ex.Message}), so it was opened as a " +
                    "temporary copy instead. Use Save as… to keep it.",
                    "Convert to PDF", DialogButtons.OK, DialogIcon.Warning);
            }
            finally
            {
                try { File.Delete(temp); } catch { /* best effort */ }
            }
        }
    }

    private async Task OpenPdfAsync(string path, bool activate = true, bool resume = false)
    {
        // Already open? Just focus it.
        var existing = Documents.FirstOrDefault(d =>
            string.Equals(d.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (existing != null) { if (activate) SelectedTab = existing; return; }

        byte[] bytes = await File.ReadAllBytesAsync(path);
        DocumentViewModel doc;

        // Password-protected? Ask, decrypt in memory, and continue normally.
        if (Graphite.Core.Pdf.PdfSecurity.IsPasswordProtected(bytes))
        {
            var decrypted = PromptAndDecrypt(bytes, Path.GetFileName(path));
            if (decrypted == null) return; // user cancelled
            bytes = decrypted.Value.Bytes;
            doc = await DocumentViewModel.FromBytesAsync(bytes, path);
            // Remember the password so saving re-encrypts instead of
            // silently stripping the file's protection.
            doc.SourcePassword = decrypted.Value.Password;
        }
        else
        {
            doc = await DocumentViewModel.FromBytesAsync(bytes, path);
        }
        ThemeService.AddRecentFile(path);
        RecentFiles.Remove(path);
        RecentFiles.Insert(0, path);

        // From the library: pick up where the person stopped. Either way, stamp "last opened"
        // so the start screen can show it under Recent.
        if (resume && ThemeService.GetReading(path) is { Page: > 0 } saved && saved.Page < doc.Pages.Count)
            doc.CurrentPageIndex = saved.Page;
        SaveReadingPosition(doc);

        Documents.Add(doc);
        if (activate) SelectedTab = doc;
    }

    private static (byte[] Bytes, string Password)? PromptAndDecrypt(byte[] bytes, string fileName)
    {
        string? error = null;
        while (true)
        {
            string? password = PasswordDialog.Show(Owner!, "Password required",
                $"“{fileName}” is password-protected. Enter the password to open it:", error);
            if (password == null) return null;
            try { return (Graphite.Core.Pdf.PdfSecurity.Decrypt(bytes, password), password); }
            catch { error = "That password didn't work — try again."; }
        }
    }

    /// <summary>Set by the window: plays the tab's closing animation; the tab is removed
    /// once the returned task completes.</summary>
    public Func<object, Task>? TabClosing { get; set; }

    [RelayCommand]
    private async Task CloseDocument(object? tab)
    {
        if (tab is WriterViewModel writer) { await CloseWriterAsync(writer); return; }
        if (tab is not DocumentViewModel doc) return;
        if (doc.IsDirty)
        {
            var answer = MessageDialog.Show(Owner,
                $"Save changes to \"{doc.Title}\" before closing?",
                "Graphite", DialogButtons.YesNoCancel, DialogIcon.Warning);
            if (answer == MessageBoxResult.Cancel) return;
            if (answer == MessageBoxResult.Yes)
            {
                // Await the save BEFORE dropping the document — the old fire-and-forget
                // call disposed the index out from under a still-running save, and a
                // failed save must abort the close.
                await SaveDocAsync(doc);
                if (doc.IsDirty) return;
            }
        }
        // Closing the active tab should land on its neighbour (what a browser does), not
        // on an empty view — the tab strip is a plain ListBox, so that is on us.
        int index = Tabs.IndexOf(doc);
        bool wasSelected = ReferenceEquals(SelectedDocument, doc);
        // Let the tab strip play its collapse animation before the tab disappears.
        if (TabClosing != null)
        {
            try { await TabClosing(doc); }
            catch (Exception ex) { App.LogError("Tab close animation failed", ex); }
        }
        SaveReadingPosition(doc);
        Documents.Remove(doc);
        if (wasSelected)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(index, 0, Tabs.Count - 1)] : null;
        doc.Dispose();
        MemoryTrim.Schedule();
    }

    private async Task CloseWriterAsync(WriterViewModel writer)
    {
        // Documents save themselves; only a failed save needs the person's say.
        if (!writer.Flush())
        {
            var answer = MessageDialog.Show(Owner,
                $"“{writer.Title}” couldn't be saved. Close it anyway and lose the latest changes?",
                "Graphite", DialogButtons.YesNo, DialogIcon.Warning);
            if (answer != MessageBoxResult.Yes) return;
        }

        int index = Tabs.IndexOf(writer);
        bool wasSelected = ReferenceEquals(SelectedWriter, writer);
        if (TabClosing != null)
        {
            try { await TabClosing(writer); }
            catch (Exception ex) { App.LogError("Tab close animation failed", ex); }
        }
        Writers.Remove(writer);
        if (wasSelected)
            SelectedTab = Tabs.Count > 0 ? Tabs[Math.Clamp(index, 0, Tabs.Count - 1)] : null;
        writer.PathChanged -= OnWriterPathChanged;
        writer.Dispose();
        MemoryTrim.Schedule();
        Library.RefreshCommand.Execute(null);
    }

    /// <summary>Ctrl+Tab / Ctrl+Shift+Tab: move to the next / previous tab, wrapping around.
    /// The start screen is the first stop, ahead of the first document.</summary>
    public void CycleDocument(int delta)
    {
        if (Tabs.Count == 0) return;
        int slots = Tabs.Count + 1; // slot 0 = Home, slot n = Tabs[n - 1]
        int current = SelectedTab == null ? 0 : Tabs.IndexOf(SelectedTab) + 1;
        int next = ((current + delta) % slots + slots) % slots;
        SelectedTab = next == 0 ? null : Tabs[next - 1];
    }

    // ------------------------------------------------------------- documents (the built-in editor)

    /// <summary>Make a new blank document in the folder the library is showing (Ctrl+N).</summary>
    [RelayCommand]
    public void NewDocument() => CreateDocumentIn(Library.NewItemFolder);

    public void CreateDocumentIn(string folder)
    {
        try
        {
            var writer = WriterViewModel.CreateNew(folder);
            AddWriter(writer);
            SelectedTab = writer;
            Library.RefreshCommand.Execute(null);
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>Open a Word file the way the person picked: in the editor, or as a PDF.</summary>
    public async Task OpenWordAsync(string path, bool inEditor)
    {
        try
        {
            if (inEditor) OpenWordInEditor(path);
            else await OpenWordAsPdfAsync(path);
        }
        catch (Exception ex) { Error(ex); }
    }

    private void OpenWordInEditor(string path)
    {
        var open = Writers.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (open != null) { SelectedTab = open; return; }

        if (GraphiteDocx.IsGraphiteDocument(path)) { OpenWriter(path, true); return; }

        // Written by Word (or anything else): the editor doesn't keep everything Word does, so it
        // works on a copy and leaves the original exactly as it is.
        string folder = Path.GetDirectoryName(path) ?? "";
        string name = Path.GetFileNameWithoutExtension(path);
        string copy = GraphiteDocx.UniquePath(folder, name + " (Graphite)", ".docx");
        var answer = MessageDialog.Show(Owner,
            $"\"{Path.GetFileName(path)}\" wasn't made in Graphite, and the editor can't keep every Word feature " +
            $"(some layout or formatting may change).\n\nEdit a copy named \"{Path.GetFileName(copy)}\"? " +
            "The original stays untouched.",
            "Edit in Graphite", DialogButtons.YesNo, DialogIcon.Info);
        if (answer != MessageBoxResult.Yes) return;

        var (document, page) = DocxCodec.Load(path);
        DocxCodec.Save(copy, document, page, name);
        GraphiteDocx.Forget(copy);
        OpenWriter(copy, true);
        Library.RefreshCommand.Execute(null);
    }

    private async Task OpenWordAsPdfAsync(string path)
    {
        var open = Writers.FirstOrDefault(w => string.Equals(w.FilePath, path, StringComparison.OrdinalIgnoreCase));
        if (open != null && !open.Flush()) return;

        try
        {
            await OpenOfficeAsPdfAsync(path);
        }
        catch (Exception ex) when (GraphiteDocx.IsGraphiteDocument(path))
        {
            // No Word here: draw the pages ourselves (images, not selectable text).
            App.LogError("Word PDF conversion unavailable, drawing pages instead", ex);
            string target = Path.Combine(Path.GetDirectoryName(path) ?? "", Path.GetFileNameWithoutExtension(path) + ".pdf");
            RenderPagesToPdf(path, target);
            await OpenPdfAsync(target);
            Library.RefreshCommand.Execute(null);
            MessageDialog.Show(Owner,
                "Word isn't installed, so the PDF holds page images rather than selectable text. " +
                "Use Recognize text (OCR) on it to make it searchable.",
                "Open as PDF", DialogButtons.OK, DialogIcon.Info);
        }
    }

    private void OpenWriter(string path, bool activate)
    {
        var writer = WriterViewModel.Open(path);
        AddWriter(writer);
        if (activate) SelectedTab = writer;
    }

    private void AddWriter(WriterViewModel writer)
    {
        writer.PathChanged += OnWriterPathChanged;
        Writers.Add(writer);
        ThemeService.AddRecentFile(writer.FilePath);
        RecentFiles.Remove(writer.FilePath);
        RecentFiles.Insert(0, writer.FilePath);
        // Stamps "last opened" so the start screen lists it under Recent.
        ThemeService.SetReading(writer.FilePath, 0, 1);
    }

    private void OnWriterPathChanged(string oldPath, string newPath)
    {
        ThemeService.RenamePath(oldPath, newPath);
        int at = RecentFiles.IndexOf(oldPath);
        if (at >= 0) RecentFiles[at] = newPath;
        Library.RefreshCommand.Execute(null);
    }

    /// <summary>Save every open document now (the window is closing). False when one could not be saved.</summary>
    public bool FlushWriters()
    {
        bool all = true;
        foreach (var writer in Writers) all &= writer.Flush();
        return all;
    }

    /// <summary>A copy of the open document under another name or in another folder.</summary>
    [RelayCommand]
    private void SaveWriterCopy()
    {
        if (SelectedWriter is not { } writer || !writer.Flush()) return;
        var dlg = new SaveFileDialog
        {
            Filter = "Word document|*.docx",
            FileName = writer.Title + " copy.docx",
            InitialDirectory = writer.FolderPath,
        };
        if (dlg.ShowDialog(Owner) != true) return;
        try
        {
            File.Copy(writer.FilePath, dlg.FileName, overwrite: true);
            GraphiteDocx.Forget(dlg.FileName);
            Library.RefreshCommand.Execute(null);
            MessageDialog.Show(Owner, $"Saved a copy to {dlg.FileName}.", "Graphite", DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private void PrintWriter()
    {
        if (SelectedWriter is not { } writer || !writer.Flush()) return;
        try
        {
            var dialog = new System.Windows.Controls.PrintDialog();
            if (dialog.ShowDialog() != true) return;
            var (copy, page) = DocxCodec.Load(writer.FilePath);
            PrepareForPages(copy, page);
            dialog.PrintDocument(((IDocumentPaginatorSource)copy).DocumentPaginator, writer.Title);
        }
        catch (Exception ex) { Error(ex); }
    }

    /// <summary>A private copy of the document laid out on fixed pages (what printing and PDF export paginate).</summary>
    private static void PrepareForPages(FlowDocument document, PageSettings page)
    {
        document.PageWidth = page.WidthDip;
        document.PageHeight = page.HeightDip;
        document.PagePadding = new Thickness(page.MarginDip);
        document.ColumnWidth = page.WidthDip;
    }

    /// <summary>Export the open document as a PDF: through Word when it is installed (real text),
    /// otherwise as page images that Graphite's OCR can make searchable.</summary>
    [RelayCommand]
    private async Task ExportWriterPdf()
    {
        if (SelectedWriter is not { } writer || !writer.Flush()) return;
        var dlg = new SaveFileDialog
        {
            Filter = "PDF document|*.pdf",
            FileName = writer.Title + ".pdf",
            InitialDirectory = writer.FolderPath,
        };
        if (dlg.ShowDialog(Owner) != true) return;

        string target = dlg.FileName;
        string source = writer.FilePath;
        bool viaOffice = false;
        var previousCursor = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            try
            {
                await Task.Run(() => OfficeToPdf.Convert(source, target));
                viaOffice = File.Exists(target);
            }
            catch (Exception ex)
            {
                // No Word (or it refused): fall back to drawing the pages ourselves.
                App.LogError("Word PDF export unavailable, drawing pages instead", ex);
            }
            if (!viaOffice) RenderPagesToPdf(source, target);

            GraphiteDocx.Forget(source);
            await OpenFilesAsync(new[] { target }, activate: false);
            Library.RefreshCommand.Execute(null);
            MessageDialog.Show(Owner,
                viaOffice
                    ? $"Exported to {target}."
                    : $"Exported to {target}.\n\nWord isn't installed, so the PDF holds page images rather than selectable text. Use Recognize text (OCR) on it to make it searchable.",
                "Export complete", DialogButtons.OK, DialogIcon.Info);
        }
        catch (Exception ex) { Error(ex); }
        finally { Mouse.OverrideCursor = previousCursor; }
    }

    private static void RenderPagesToPdf(string docxPath, string pdfPath)
    {
        var (copy, page) = DocxCodec.Load(docxPath);
        PrepareForPages(copy, page);
        var paginator = ((IDocumentPaginatorSource)copy).DocumentPaginator;
        paginator.ComputePageCount();

        string temp = Path.Combine(Path.GetTempPath(), "graphite-export-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            const double scale = 2.0;
            var images = new List<string>();
            for (int i = 0; i < Math.Max(1, paginator.PageCount); i++)
            {
                using DocumentPage documentPage = paginator.GetPage(i);
                var bitmap = new RenderTargetBitmap((int)Math.Ceiling(page.WidthDip * scale), (int)Math.Ceiling(page.HeightDip * scale),
                    96 * scale, 96 * scale, PixelFormats.Pbgra32);
                var paper = new DrawingVisual();
                using (var dc = paper.RenderOpen())
                    dc.DrawRectangle(Brushes.White, null, new Rect(0, 0, page.WidthDip, page.HeightDip));
                bitmap.Render(paper);
                bitmap.Render(documentPage.Visual);

                var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                string file = Path.Combine(temp, $"page{i + 1}.jpg");
                using (var fs = File.Create(file)) encoder.Save(fs);
                images.Add(file);
            }
            File.WriteAllBytes(pdfPath, PageOperations.FromImages(images, page.WidthPt, page.HeightPt));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* best effort */ }
        }
    }

    // ------------------------------------------------------------- save

    [RelayCommand]
    private Task Save()
    {
        if (SelectedWriter is { } writer)
        {
            writer.Save();
            return Task.CompletedTask;
        }
        return SelectedDocument == null ? Task.CompletedTask : SaveDocAsync(SelectedDocument);
    }

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
        if (SelectedWriter != null) { SaveWriterCopy(); return; }
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
            await doc.ApplyOperationAsync("Deleting pages…", b => PageOperations.DeletePages(b, pages),
                new PageOpHint(PageOpKind.Delete, pages.Min()));
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
            await doc.ApplyOperationAsync("Inserting pages…", b => PageOperations.InsertPdf(b, at, other),
                new PageOpHint(PageOpKind.Insert, at));
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
            await doc.ApplyOperationAsync("Inserting page…", b => PageOperations.InsertBlankPage(b, at, w, h),
                new PageOpHint(PageOpKind.Insert, at));
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
        try
        {
            await doc.ApplyOperationAsync("Rotating…", b => PageOperations.RotatePage(b, index, delta),
                new PageOpHint(PageOpKind.Rotate, index, 1, delta));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task MovePageUp(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null || page.Index == 0) return;
        try
        {
            await doc.ApplyOperationAsync("Reordering…", b => PageOperations.MovePage(b, page.Index, page.Index - 1),
                new PageOpHint(PageOpKind.Move, page.Index, 1, page.Index - 1));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task MovePageDown(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null || page.Index >= doc.Pages.Count - 1) return;
        try
        {
            await doc.ApplyOperationAsync("Reordering…", b => PageOperations.MovePage(b, page.Index, page.Index + 1),
                new PageOpHint(PageOpKind.Move, page.Index, 1, page.Index + 1));
        }
        catch (Exception ex) { Error(ex); }
    }

    [RelayCommand]
    private async Task DeletePage(PageViewModel? page)
    {
        if (SelectedDocument is not { } doc || page == null) return;
        if (doc.Pages.Count <= 1) return;
        try
        {
            await doc.ApplyOperationAsync("Deleting page…", b => PageOperations.DeletePages(b, new[] { page.Index }),
                new PageOpHint(PageOpKind.Delete, page.Index));
        }
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
        if (SelectedDocument is not { } d) return;
        // With the Lasso tool and something grabbed, the colour menu recolours the selection.
        if (d.ActiveTool == ToolKind.Lasso && d.HasLassoSelection && d.RecolorLassoSelection(hex))
            return;
        d.ActiveColorHex = hex;
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

    [RelayCommand]
    private void ZoomIn()
    {
        if (SelectedDocument is { } d) d.Zoom = Math.Min(6, d.Zoom * 1.2);
        else if (SelectedWriter is { } w) w.Zoom = Math.Round(w.Zoom + 0.1, 2);
    }

    [RelayCommand]
    private void ZoomOut()
    {
        if (SelectedDocument is { } d) d.Zoom = Math.Max(0.25, d.Zoom / 1.2);
        else if (SelectedWriter is { } w) w.Zoom = Math.Round(w.Zoom - 0.1, 2);
    }

    [RelayCommand]
    private void GoToPageDialog()
    {
        if (SelectedDocument is not { } doc) return;
        string? input = InputDialog.Show(Owner!, "Go to page", $"Page number (1–{doc.Pages.Count}):",
            (doc.CurrentPageIndex + 1).ToString());
        if (input != null && int.TryParse(input, out int page))
            doc.GoToPage(page - 1, flash: true);
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
        if (SelectedWriter != null) { PrintWriter(); return; }
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

    // ------------------------------------------------------------- updates

    /// <summary>Running build's version, shown on the start page.</summary>
    public string AppVersion { get; } = UpdateService.CurrentVersion.ToString(3);

    [RelayCommand]
    private Task CheckForUpdates() => CheckForUpdatesAsync(silent: false);

    /// <summary>Silent checks (app start) only surface a dialog when an update exists;
    /// manual checks (menu / palette) also report "up to date" and errors.</summary>
    public async Task CheckForUpdatesAsync(bool silent)
    {
        try
        {
            var info = await UpdateService.CheckAsync();
            if (info == null)
            {
                if (!silent)
                    MessageDialog.Show(Owner, $"Graphite {AppVersion} is up to date.",
                        "Updates", DialogButtons.OK, DialogIcon.Info);
                return;
            }

            // The quiet startup check shows a small non-blocking toast; a manual check
            // (menu / palette) keeps the explicit dialog.
            if (silent && UpdateAvailable != null)
            {
                UpdateAvailable.Invoke(info.Tag.TrimStart('v', 'V'), () => InstallUpdateAsync(info));
                return;
            }

            var answer = MessageDialog.Show(Owner,
                $"Graphite {info.Tag.TrimStart('v', 'V')} is available — you're running {AppVersion}.\n\n" +
                "Download and install it now? Graphite will restart to finish the update.",
                "Update available", DialogButtons.YesNo, DialogIcon.Info);
            if (answer != MessageBoxResult.Yes) return;

            await InstallUpdateAsync(info);
        }
        catch (Exception ex)
        {
            if (!silent) Error(ex);
        }
    }

    /// <summary>Raised (instead of a dialog) when the quiet startup check finds a newer
    /// release: the new version and the action that installs it.</summary>
    public event Action<string, Func<Task>>? UpdateAvailable;

    private static async Task InstallUpdateAsync(UpdateInfo info)
    {
        if (info.ZipUrl == null)
        {
            UpdateService.OpenReleasePage(info);
            return;
        }

        try
        {
            await UpdateService.DownloadAndRestartAsync(info);
        }
        catch (Exception ex)
        {
            // In-place update failed (offline mid-download, unwritable install
            // folder, …) — fall back to the release page so the user can update
            // manually.
            var fallback = MessageDialog.Show(Owner,
                $"The automatic update couldn't finish ({ex.Message}).\n\nOpen the download page instead?",
                "Update failed", DialogButtons.YesNo, DialogIcon.Warning);
            if (fallback == MessageBoxResult.Yes)
                UpdateService.OpenReleasePage(info);
        }
    }

    // ------------------------------------------------------------- command palette

    /// <summary>Raised by the command palette; the window performs the actual paste
    /// (clipboard access and PendingImage creation live in the view layer).</summary>
    public event Action? PasteImageRequested;

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
            new("Open document…", "Ctrl+O", () => _ = Open()),
            new("New document", "Ctrl+N", NewDocument),
            new("New folder", "Ctrl+Shift+N", () => Library.NewFolderCommand.Execute(null)),
            new("Go to Home (library)", null, GoHome),
            new("Choose library folder…", null, () => Library.ChooseFolderCommand.Execute(null)),
            new("Merge PDFs…", null, () => _ = Merge()),
            new("Toggle theme", null, ToggleTheme),
            new("Toggle fullscreen", "F11", ToggleFullscreen),
            new("Edit signature…", null, EditSignature),
            new("Toggle reduced motion", null, () => ThemeService.SetReduceMotion(!ThemeService.ReduceMotion)),
            new("Toggle sidebar", null, () => ShowSidebar = !ShowSidebar),
            new("Toggle markup panel", null, () => ShowInspector = !ShowInspector),
            new("Check for updates…", null, () => _ = CheckForUpdates()),
        };

        if (SelectedWriter is { } writer)
        {
            list.InsertRange(1, new PaletteCommand[]
            {
                new("Save a copy…", "Ctrl+Shift+S", SaveWriterCopy),
                new("Print…", "Ctrl+P", PrintWriter),
                new("Export document as PDF…", null, () => _ = ExportWriterPdf()),
                new("Toggle outline", null, () => writer.ShowOutline = !writer.ShowOutline),
                new("Next tab", "Ctrl+Tab", () => CycleDocument(+1)),
                new("Previous tab", "Ctrl+Shift+Tab", () => CycleDocument(-1)),
                new("Close document", "Ctrl+W", () => _ = CloseWriterAsync(writer)),
            });
        }

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
                new("Paste image from clipboard", "Ctrl+V", () => PasteImageRequested?.Invoke()),
                new("Next tab", "Ctrl+Tab", () => CycleDocument(+1)),
                new("Previous tab", "Ctrl+Shift+Tab", () => CycleDocument(-1)),
                new("Close document", "Ctrl+W", () => _ = CloseDocument(doc)),
            });
        }

        return list;
    }
}
