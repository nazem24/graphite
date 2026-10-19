using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Graphite.App.Services;
using Graphite.App.Views;
using Graphite.Core.Canvas;
using Graphite.Core.Library;
using Microsoft.Win32;

namespace Graphite.App.ViewModels;

/// <summary>A term in the settings' term picker; a null id means "the term running today".</summary>
public sealed record CanvasTermOption(string? Id, string Name);

/// <summary>A course that is linked to a folder, in Settings › Canvas.</summary>
public sealed class LinkedCourseRow
{
    public LinkedCourseRow(string courseId, string name, string folder)
    {
        CourseId = courseId;
        Name = name;
        Folder = folder;
    }

    public string CourseId { get; }
    public string Name { get; }
    public string Folder { get; }
    public bool FolderExists => Directory.Exists(Folder);
    public string FolderLine => FolderExists ? Folder : Folder + "  (folder not found)";
}

/// <summary>Settings › Canvas: connection (address and token), term, linked folders, the two switches and the cache.</summary>
public sealed partial class SettingsViewModel
{
    [ObservableProperty] private string canvasBaseUrl = CanvasSettings.BaseUrl;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCanvasMessage))]
    private string canvasMessage = "";

    [ObservableProperty] private bool canvasMessageIsError;
    [ObservableProperty] private bool isTestingCanvas;

    public bool HasCanvasMessage => CanvasMessage.Length > 0;
    public bool HasCanvasToken => CanvasSettings.HasToken;

    /// <summary>"Connected as Michael Zihlmann", or how to connect.</summary>
    public string CanvasConnectionText => CanvasSettings.HasToken
        ? (CanvasSettings.AccountName is { Length: > 0 } name ? $"Connected as {name}" : "A token is saved")
        : "Not connected";

    public string CanvasTokenHint => CanvasSettings.HasToken
        ? "Paste a new token to replace the saved one. It is stored encrypted for your Windows account, never in plain text, and only ever sent to the Canvas address above."
        : "Stored encrypted for your Windows account, never in plain text, and only ever sent to the Canvas address above.";

    public ObservableCollection<CanvasTermOption> CanvasTerms { get; } = new();
    public ObservableCollection<LinkedCourseRow> LinkedCourses { get; } = new();
    public bool HasLinkedCourses => LinkedCourses.Count > 0;

    private bool _fillingTerms;
    [ObservableProperty] private CanvasTermOption? selectedCanvasTerm;

    partial void OnSelectedCanvasTermChanged(CanvasTermOption? value)
    {
        if (_fillingTerms || value == null) return;
        CanvasSettings.SetTerm(value.Id);
    }

    public bool CanvasCheckAllCourses
    {
        get => CanvasSettings.CheckAllCourses;
        set
        {
            CanvasSettings.SetCheckAllCourses(value);
            OnPropertyChanged();
        }
    }

    public bool CanvasAllFileTypes
    {
        get => CanvasSettings.AllFileTypes;
        set
        {
            CanvasSettings.SetAllFileTypes(value);
            OnPropertyChanged();
        }
    }

    [ObservableProperty] private string canvasCacheText = "";

    /// <summary>Re-read everything Canvas shows (the page was opened, or something changed).</summary>
    private void RefreshCanvas()
    {
        CanvasBaseUrl = CanvasSettings.BaseUrl;
        var courses = new CanvasCache(CanvasSettings.CacheRoot).LoadCourses()?.Courses ?? new List<CanvasCourse>();

        _fillingTerms = true;
        CanvasTerms.Clear();
        CanvasTerms.Add(new CanvasTermOption(null, "The current term (changes by itself)"));
        foreach (var t in CanvasParser.Terms(courses)) CanvasTerms.Add(new CanvasTermOption(t.Id, t.Name));
        SelectedCanvasTerm = CanvasTerms.FirstOrDefault(t => t.Id == CanvasSettings.TermId) ?? CanvasTerms[0];
        _fillingTerms = false;

        LinkedCourses.Clear();
        foreach (var (id, folder) in CanvasSettings.CourseFolders.OrderBy(kv => kv.Key))
        {
            string name = courses.FirstOrDefault(c => c.Id == id)?.Name ?? $"Course {id}";
            LinkedCourses.Add(new LinkedCourseRow(id, name, folder));
        }
        OnPropertyChanged(nameof(HasLinkedCourses));
        OnPropertyChanged(nameof(HasCanvasToken));
        OnPropertyChanged(nameof(CanvasConnectionText));
        OnPropertyChanged(nameof(CanvasTokenHint));
        OnPropertyChanged(nameof(CanvasCheckAllCourses));
        OnPropertyChanged(nameof(CanvasAllFileTypes));
        long size = CanvasSettings.CacheSize();
        CanvasCacheText = size == 0 ? "No downloaded files in the cache." : $"Downloaded files in the cache: {LibraryScanner.FormatSize(size)}.";
    }

    /// <summary>Check the token against Canvas and keep it when it works. Called by the page with the password
    /// box's text (a password box cannot be bound). Returns true when it was saved.</summary>
    public async Task<bool> TestAndSaveCanvasAsync(string token)
    {
        token = token.Trim();
        string address = CanvasBaseUrl.Trim();
        if (token.Length == 0 && CanvasSettings.HasToken)
        {
            // only the address changed: keep the saved token
            token = CanvasSettings.ReadToken() ?? "";
        }
        if (token.Length == 0)
        {
            ShowCanvasMessage("Paste your access token first.", error: true);
            return false;
        }

        IsTestingCanvas = true;
        ShowCanvasMessage("Checking with Canvas…", error: false);
        try
        {
            using var client = new CanvasClient(address, token);
            string name = await CanvasFetcher.WhoAmIAsync(client);
            CanvasSettings.SetConnection(client.BaseUrl, token, name);
            ShowCanvasMessage($"Connected as {name}. Your courses are in the Canvas space of the start page.", error: false);
            RefreshCanvas();
            return true;
        }
        catch (CanvasApiException ex) when (ex.Status == 401)
        {
            ShowCanvasMessage("Canvas rejected this token. Check that it was copied completely and has not expired.", error: true);
        }
        catch (CanvasApiException ex)
        {
            ShowCanvasMessage("Canvas said: " + ex.Message, error: true);
        }
        catch (HttpRequestException)
        {
            ShowCanvasMessage("Couldn't reach that address. Check the address and your internet connection.", error: true);
        }
        catch (ArgumentException ex)
        {
            ShowCanvasMessage(ex.Message, error: true);
        }
        finally
        {
            IsTestingCanvas = false;
        }
        return false;
    }

    private void ShowCanvasMessage(string text, bool error)
    {
        CanvasMessageIsError = error;
        CanvasMessage = text;
    }

    [RelayCommand]
    private void RemoveCanvasToken()
    {
        if (!CanvasSettings.HasToken) return;
        if (MessageDialog.Show(Application.Current?.MainWindow,
                "Remove the saved Canvas token? Graphite disconnects from Canvas; cached courses and linked folders stay. You can delete the token in Canvas too (Account › Settings).",
                "Canvas", DialogButtons.YesNo, DialogIcon.Info) != MessageBoxResult.Yes)
            return;
        CanvasSettings.RemoveToken();
        ShowCanvasMessage("The token was removed.", error: false);
        RefreshCanvas();
    }

    /// <summary>Canvas › Account › Settings, where tokens are made.</summary>
    [RelayCommand]
    private void OpenCanvasTokenPage()
    {
        string address;
        try { address = CanvasClient.NormalizeBase(CanvasBaseUrl).AbsoluteUri.TrimEnd('/'); }
        catch (ArgumentException) { address = CanvasClient.DefaultBaseUrl; }
        try { Process.Start(new ProcessStartInfo(address + "/profile/settings") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.LogError("Opening the Canvas settings page failed", ex);
        }
    }

    [RelayCommand]
    private void ChangeLinkedFolder(LinkedCourseRow? row)
    {
        if (row == null) return;
        var dialog = new OpenFolderDialog
        {
            Title = $"Link “{row.Name}” to a folder",
            InitialDirectory = row.FolderExists ? row.Folder : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        var manifest = CanvasFolderLink.Load(dialog.FolderName);
        manifest.CourseId = row.CourseId;
        CanvasFolderLink.Save(dialog.FolderName, manifest);
        CanvasSettings.SetFolder(row.CourseId, dialog.FolderName);
        RefreshCanvas();
    }

    [RelayCommand]
    private void UnlinkCourse(LinkedCourseRow? row)
    {
        if (row == null) return;
        CanvasSettings.SetFolder(row.CourseId, null);
        RefreshCanvas();
    }

    [RelayCommand]
    private void OpenLinkedFolder(LinkedCourseRow? row)
    {
        if (row is not { FolderExists: true }) return;
        try { Process.Start("explorer.exe", $"\"{row.Folder}\""); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            App.LogError("Show in Explorer failed", ex);
        }
    }

    [RelayCommand]
    private void ClearCanvasCache()
    {
        if (MessageDialog.Show(Application.Current?.MainWindow,
                "Delete the cached course lists and the files downloaded to the cache? Files in linked folders, pins and what you have seen are kept. Courses load from Canvas again the next time you open them.",
                "Canvas", DialogButtons.YesNo, DialogIcon.Info) != MessageBoxResult.Yes)
            return;
        CanvasSettings.ClearCache();
        RefreshCanvas();
    }
}
