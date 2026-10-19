using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core;

namespace Graphite.App;

public partial class App : Application
{
    private static readonly string LogDir =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Graphite");

    private static readonly string LogPath = Path.Combine(LogDir, "graphite.log");
    private static readonly object LogGate = new();

    /// <summary>Owns the per-session "one Graphite" mutex and pipe for the app's lifetime.</summary>
    private static SingleInstance? _instance;

    protected override void OnStartup(StartupEventArgs e)
    {
        // Drawing on the processor or on the graphics card (decided before any window exists). On ARM PCs the graphics
        // path of WPF stutters while scrolling (measured on a Surface: more late frames, and it felt worse), and a
        // current ARM processor draws this app without trouble, so ARM64 builds draw on the processor. Everything else
        // keeps the graphics card. GRAPHITE_RENDER=software or =hardware overrides either way.
        string? render = Environment.GetEnvironmentVariable("GRAPHITE_RENDER");
        bool software = string.Equals(render, "software", StringComparison.OrdinalIgnoreCase)
            || (!string.Equals(render, "hardware", StringComparison.OrdinalIgnoreCase)
                && System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64);
        if (software)
            System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;

        base.OnStartup(e);

        // Files passed on the command line (file association / "Open with"), as full paths —
        // they may be handed to another process whose working directory differs.
        var pdfArgs = e.Args.Where(File.Exists).Select(Path.GetFullPath).ToArray();

        // Graphite is already running: give it the files and bow out. (If it doesn't answer,
        // carry on as a normal window instead of dropping the files.)
        _instance = new SingleInstance();
        if (!_instance.IsFirst && _instance.Forward(pdfArgs))
        {
            Shutdown();
            return;
        }

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

        // The person got past SmartScreen's "Windows protected your PC" prompt to be here: drop the download mark from
        // Graphite's own files so the prompt doesn't come back on every launch.
        MarkOfTheWeb.UnblockInBackground(AppContext.BaseDirectory);

        CleanupStalePasteFiles();
        RecoveryService.StartSession();
        ThemeService.Initialize();
        IconMotion.Register();

        // Tooltips appear after the pointer has rested for 0.9 s (name, key and a one-line hint).
        try
        {
            System.Windows.Controls.ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject), new FrameworkPropertyMetadata(900));
        }
        catch (Exception ex) { LogError("Could not set the tooltip delay", ex); }

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Later launches hand their files over the pipe: open them here and come to the front.
        _instance.Listen(paths => window.Dispatcher.BeginInvoke(() =>
        {
            window.BringToFront();
            if (paths.Length > 0) _ = window.ViewModel.OpenFilesAsync(paths);
        }));

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
        // A clean exit: nothing to recover next time (the person already chose to save or discard).
        RecoveryService.EndSession();
        ThemeService.Flush(); // settings changed in the last moment are still waiting for their write
        _instance?.Dispose();
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
