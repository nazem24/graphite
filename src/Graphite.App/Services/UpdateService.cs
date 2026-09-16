using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Windows;

namespace Graphite.App.Services;

public sealed record UpdateInfo(string Tag, Version Version, string? ZipUrl, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer Graphite build and can self-update in place.
/// The release workflow publishes a Graphite-vX.Y.Z-win-x64.zip asset per tag; the
/// updater downloads that zip, extracts it to a temp folder, and hands off to a tiny
/// PowerShell script that waits for this process to exit, copies the new files over
/// the install directory, and relaunches the app (a running exe can't overwrite itself).
/// </summary>
public static class UpdateService
{
    private const string Repo = "nazem24/graphite";

    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Graphite-Updater");
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    /// <summary>The running build's version, taken from the assembly informational
    /// version (stamped from the release tag by the CI workflow).</summary>
    public static Version CurrentVersion { get; } = ReadCurrentVersion();

    private static Version ReadCurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly();
        string? text = asm?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                       ?? asm?.GetName().Version?.ToString();
        // Strip any semver suffixes ("1.3.0+abc123") before parsing.
        text = text?.Split('+', '-')[0];
        return Version.TryParse(text, out var v) ? v : new Version(0, 0, 0);
    }

    /// <summary>Returns info about the latest release when it is newer than the running
    /// build; null when already up to date. Throws on network/API errors.</summary>
    public static async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        string json = await Http.GetStringAsync(
            $"https://api.github.com/repos/{Repo}/releases/latest", ct);

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = root.GetProperty("tag_name").GetString() ?? "";
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var latest)) return null;
        if (latest <= CurrentVersion) return null;

        string? zip = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (url != null && url.Contains("win-x64", StringComparison.OrdinalIgnoreCase)
                                && url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                {
                    zip = url;
                    break;
                }
            }
        }

        string releaseUrl = root.TryGetProperty("html_url", out var h) && h.GetString() is { } page
            ? page
            : $"https://github.com/{Repo}/releases/latest";

        return new UpdateInfo(tag, latest, zip, releaseUrl);
    }

    /// <summary>Downloads the release zip, stages the updater script, and shuts the app
    /// down so the script can swap the files and relaunch.</summary>
    public static async Task DownloadAndRestartAsync(UpdateInfo info)
    {
        if (info.ZipUrl == null)
            throw new InvalidOperationException("This release has no downloadable package.");

        string stageDir = Path.Combine(Path.GetTempPath(), $"graphite-update-{info.Tag}");
        string zipPath = stageDir + ".zip";

        byte[] bytes = await Http.GetByteArrayAsync(info.ZipUrl);
        await File.WriteAllBytesAsync(zipPath, bytes);

        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, stageDir);

        string appDir = AppContext.BaseDirectory;
        string exe = Path.Combine(appDir, "Graphite.exe");
        string scriptPath = Path.Combine(Path.GetTempPath(), $"graphite-update-{info.Tag}.ps1");

        // Single quotes in PowerShell are literal — escape any embedded ones by doubling.
        string Ps(string s) => s.Replace("'", "''");

        await File.WriteAllTextAsync(scriptPath, $$"""
            $appPid = {{Environment.ProcessId}}
            while (Get-Process -Id $appPid -ErrorAction SilentlyContinue) { Start-Sleep -Milliseconds 300 }
            Start-Sleep -Milliseconds 500
            Copy-Item -Path '{{Ps(stageDir)}}\*' -Destination '{{Ps(appDir)}}' -Recurse -Force
            Start-Process -FilePath '{{Ps(exe)}}'
            Remove-Item -Path '{{Ps(stageDir)}}' -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -Path '{{Ps(zipPath)}}' -Force -ErrorAction SilentlyContinue
            Remove-Item -Path $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """);

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
    }

    /// <summary>Opens the release page in the default browser (fallback when the
    /// in-place update can't run, e.g. no zip asset or an unwritable install dir).</summary>
    public static void OpenReleasePage(UpdateInfo info) =>
        Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
}
