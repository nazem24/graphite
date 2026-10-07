using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Graphite.App.Services;
using Graphite.App.Views;

namespace Graphite.App;

public partial class App : Application
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Graphite");

    private static readonly string LogPath = Path.Combine(LogDir, "graphite.log");
    private static readonly object LogGate = new();

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            LogError("Unhandled UI exception", args.Exception);
            args.Handled = true;
            try
            {
                MessageDialog.Show(MainWindow, args.Exception.Message, "Unexpected error",
                    DialogButtons.OK, DialogIcon.Error);
            }
            catch { /* never let the error dialog itself take the app down */ }
        };

        // Exceptions from fire-and-forget tasks (`_ = SomethingAsync()`) never reach the
        // dispatcher handler. Log them and mark them observed instead of losing them.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            LogError("Unobserved task exception", args.Exception);
            args.SetObserved();
        };

        // Last chance (background-thread crash): at least leave a trace on disk.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            LogError("Fatal unhandled exception", args.ExceptionObject as Exception);

        // Start loading PDFium / the PDF readers now, in the background, so the first document
        // the user opens doesn't pay for it.
        Warmup.Start();

        CleanupStalePasteFiles();
        ThemeService.Initialize();
        IconMotion.Register();

        // Tooltips appear after 400 ms of hovering (name, key and a one-line hint).
        try
        {
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject), new FrameworkPropertyMetadata(400));
        }
        catch (Exception ex) { LogError("Could not set the tooltip delay", ex); }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Files passed on the command line (file association / "Open with").
        var pdfArgs = e.Args.Where(File.Exists).ToArray();
        if (pdfArgs.Length > 0)
            _ = window.ViewModel.OpenFilesAsync(pdfArgs);
        else
            // Build the document view once while the app is idle (and discard it), so the
            // first Open doesn't also pay for loading its whole XAML template.
            window.Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, new Action(() => PrewarmDocumentTemplate(window)));
    }

    private static void PrewarmDocumentTemplate(MainWindow window)
    {
        try
        {
            if (window.ViewModel.Documents.Count == 0 && window.TryFindResource("DocTemplate") is DataTemplate template)
                template.LoadContent();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Template warm-up skipped: {ex.Message}");
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        foreach (var f in PasteTempFiles)
            try { File.Delete(f); } catch { /* best effort */ }
        base.OnExit(e);
    }

    // ------------------------------------------------------------- diagnostics

    /// <summary>Append an error to %LOCALAPPDATA%\Graphite\graphite.log (rotated at 1 MB).
    /// Never throws — logging must not be able to cause a second failure.</summary>
    public static void LogError(string context, Exception? ex)
    {
        System.Diagnostics.Debug.WriteLine($"{context}: {ex}");
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(LogDir);
                var info = new FileInfo(LogPath);
                if (info.Exists && info.Length > 1024 * 1024)
                    File.Move(LogPath, LogPath + ".old", overwrite: true);
                File.AppendAllText(LogPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] v{UpdateService.CurrentVersion} {context}{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
            }
        }
        catch { /* ignore */ }
    }

    // ------------------------------------------------------------- pasted images

    private static readonly List<string> PasteTempFiles = new();

    /// <summary>Path for a pasted clipboard image. Pasted images stay temp files until the
    /// document is saved (they're baked in then); they used to pile up in %TEMP% forever.</summary>
    public static string NewPasteTempFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"graphite-paste-{Guid.NewGuid():N}.png");
        lock (PasteTempFiles) PasteTempFiles.Add(path);
        return path;
    }

    /// <summary>Remove paste files left behind by earlier sessions (e.g. after a crash).</summary>
    private static void CleanupStalePasteFiles()
    {
        try
        {
            var cutoff = DateTime.Now.AddDays(-2);
            foreach (var f in Directory.EnumerateFiles(Path.GetTempPath(), "graphite-paste-*.png"))
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); } catch { /* in use */ }
        }
        catch { /* best effort */ }
    }
}
