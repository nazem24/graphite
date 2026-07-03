using System.IO;
using System.Windows;
using Graphite.App.Services;
using Graphite.App.Views;

namespace Graphite.App;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            MessageDialog.Show(MainWindow, args.Exception.Message, "Unexpected error",
                DialogButtons.OK, DialogIcon.Error);
            args.Handled = true;
        };

        ThemeService.Initialize();

        var window = new MainWindow();
        MainWindow = window;
        window.Show();

        // Files passed on the command line (file association / "Open with").
        var pdfArgs = e.Args.Where(File.Exists).ToArray();
        if (pdfArgs.Length > 0)
            _ = window.ViewModel.OpenFilesAsync(pdfArgs);
    }
}
