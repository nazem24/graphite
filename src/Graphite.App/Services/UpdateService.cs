using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;

namespace Graphite.App.Services;

/// <param name="ZipUrl">The release package; null when the release has none (or no checksum
/// to verify it against), in which case updating falls back to the release page.</param>
/// <param name="ChecksumUrl">The package's published SHA-256 (<c>….zip.sha256</c>).</param>
public sealed record UpdateInfo(string Tag, Version Version, string? ZipUrl, string? ChecksumUrl, string ReleaseUrl);

/// <summary>
/// Checks GitHub Releases for a newer Graphite build and can self-update in place.
/// The release workflow publishes a Graphite-vX.Y.Z-win-x64.zip and a Graphite-vX.Y.Z-win-arm64.zip asset per tag, their SHA-256
/// (….zip.sha256) and, inside the zip, a manifest of the files it contains (Graphite.files).
///
/// Updating: download the zip, verify it against the published checksum (a damaged or
/// truncated download is never installed), check it really is a Graphite package, extract it,
/// and hand off to a small PowerShell script that waits for this process to exit and then
/// installs transactionally — every file it will overwrite or remove is backed up first, files
/// the previous version shipped but the new one doesn't are removed (only those: the install
/// folder may hold unrelated files), and any failure restores the backup so the old version
/// keeps working. The script relaunches the app either way and leaves a note for it when the
/// update failed (<see cref="TakeFailedUpdateNote"/>).
/// </summary>
public static class UpdateService
{
    private const string Repo = "nazem24/graphite";

    /// <summary>The package flavour of the running build ("win-x64" or "win-arm64"). An update stays on the line it is on:
    /// an x64 build running on an ARM PC keeps getting x64 packages (they have OCR); a person who wants the native ARM64 build
    /// installs that package once, and it then updates itself with ARM64 packages.</summary>
    private static string PackageFlavour =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.Arm64
            ? "win-arm64"
            : "win-x64";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>Separate client for the package download: no overall timeout (the 15-minute
    /// cap is applied per download via a cancellation token instead).</summary>
    private static readonly HttpClient Download = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var http = new HttpClient { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Graphite-Updater");
        return http;
    }

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

        string? zip = null, checksum = null;
        if (root.TryGetProperty("assets", out var assets))
        {
            foreach (var asset in assets.EnumerateArray())
            {
                string? url = asset.GetProperty("browser_download_url").GetString();
                if (url == null || !url.Contains(PackageFlavour, StringComparison.OrdinalIgnoreCase)) continue;
                if (url.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) zip ??= url;
                else if (url.EndsWith(".zip.sha256", StringComparison.OrdinalIgnoreCase)) checksum ??= url;
            }
        }

        string releaseUrl = root.TryGetProperty("html_url", out var h) && h.GetString() is { } page
            ? page
            : $"https://github.com/{Repo}/releases/latest";

        // Without a checksum the package can't be verified: don't install it in place, send the
        // person to the release page instead.
        return new UpdateInfo(tag, latest, checksum != null ? zip : null, checksum, releaseUrl);
    }

    /// <summary>Downloads and verifies the release zip, stages the installer script, and shuts the
    /// app down so the script can swap the files and relaunch.</summary>
    public static async Task DownloadAndRestartAsync(UpdateInfo info)
    {
        if (info.ZipUrl == null || info.ChecksumUrl == null)
            throw new InvalidOperationException("This release has no verifiable package.");

        string stageDir = Path.Combine(Path.GetTempPath(), $"graphite-update-{info.Tag}");
        string zipPath = stageDir + ".zip";

        // The published checksum: "<64 hex digits>  Graphite-vX.Y.Z-win-x64.zip" (or -win-arm64).
        string checksumText = await Http.GetStringAsync(info.ChecksumUrl);
        string expected = checksumText.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
        if (expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidDataException("The release's checksum file is malformed.");

        // Stream the package to disk. GetByteArrayAsync buffered the whole ~70 MB zip in
        // memory and ran under the 20-second API timeout, so the update failed on any
        // connection slower than ~30 Mbit/s.
        using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(15)))
        using (var response = await Download.GetAsync(info.ZipUrl, HttpCompletionOption.ResponseHeadersRead, cts.Token))
        {
            response.EnsureSuccessStatusCode();
            await using var source = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var target = File.Create(zipPath);
            await source.CopyToAsync(target, cts.Token);
        }

        string actual;
        await using (var file = File.OpenRead(zipPath))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(file));
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            TryDelete(zipPath);
            throw new InvalidDataException(
                "The downloaded update is damaged (its checksum doesn't match the published one), so it wasn't installed.");
        }

        if (Directory.Exists(stageDir)) Directory.Delete(stageDir, recursive: true);
        ZipFile.ExtractToDirectory(zipPath, stageDir);
        if (!File.Exists(Path.Combine(stageDir, "Graphite.exe")))
        {
            TryDelete(zipPath);
            try { Directory.Delete(stageDir, recursive: true); } catch { /* best effort */ }
            throw new InvalidDataException("The downloaded package doesn't contain Graphite.exe.");
        }

        string appDir = AppContext.BaseDirectory.TrimEnd('\\', '/');
        string scriptPath = Path.Combine(Path.GetTempPath(), $"graphite-update-{info.Tag}.ps1");
        await File.WriteAllTextAsync(scriptPath, InstallScript(Environment.ProcessId, appDir, stageDir, zipPath, info.Tag));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
        });

        Application.Current.Dispatcher.Invoke(Application.Current.Shutdown);
    }

    /// <summary>File (in %LOCALAPPDATA%\Graphite) the install script writes when it had to roll back.</summary>
    private static string FailedUpdateNotePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Graphite", "update-failed.txt");

    /// <summary>The reason the last update was rolled back, if it was (read once, then cleared).</summary>
    public static string? TakeFailedUpdateNote()
    {
        try
        {
            if (!File.Exists(FailedUpdateNotePath)) return null;
            string text = File.ReadAllText(FailedUpdateNotePath).Trim();
            File.Delete(FailedUpdateNotePath);
            return text.Length > 0 ? text : "The update couldn't be installed.";
        }
        catch { return null; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* best effort */ }
    }

    /// <summary>The PowerShell installer (Windows PowerShell 5.1 compatible). Runs after Graphite exits.</summary>
    internal static string InstallScript(int pid, string appDir, string stageDir, string zipPath, string tag)
    {
        // Single quotes in PowerShell are literal — escape any embedded ones by doubling.
        static string Ps(string s) => s.Replace("'", "''");

        return $$"""
            $ErrorActionPreference = 'Stop'
            $appPid = {{pid}}
            $appDir = '{{Ps(appDir)}}'
            $stage = '{{Ps(stageDir)}}'
            $zip = '{{Ps(zipPath)}}'
            $exe = Join-Path $appDir 'Graphite.exe'
            $dataDir = Join-Path $env:LOCALAPPDATA 'Graphite'
            $log = Join-Path $dataDir 'update.log'
            $failNote = Join-Path $dataDir 'update-failed.txt'
            New-Item -ItemType Directory -Force -Path $dataDir | Out-Null
            function Log($message) { try { Add-Content -LiteralPath $log -Value ('[' + (Get-Date -Format s) + '] ' + $message) } catch { } }

            # Wait for Graphite to exit (at most a minute).
            $deadline = (Get-Date).AddSeconds(60)
            while ((Get-Process -Id $appPid -ErrorAction SilentlyContinue) -and ((Get-Date) -lt $deadline)) { Start-Sleep -Milliseconds 300 }
            Start-Sleep -Milliseconds 500

            # Files of the new version, and of the old one when it shipped a manifest. Only files the
            # old version itself installed are ever removed: the folder may hold unrelated things.
            # Normalise the folder paths the way Get-ChildItem reports children (8.3 short names in
            # %TEMP% otherwise make the relative-path arithmetic below come out wrong).
            $stage = (Get-Item -LiteralPath $stage).FullName.TrimEnd('\')
            $newFiles = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object { $_.FullName.Substring($stage.Length).TrimStart('\') })
            $manifest = Join-Path $appDir 'Graphite.files'
            $oldFiles = @()
            if (Test-Path -LiteralPath $manifest) {
                $oldFiles = @(Get-Content -LiteralPath $manifest | Where-Object { $_ -and -not $_.Contains('..') -and -not [System.IO.Path]::IsPathRooted($_) })
            }
            $stale = @($oldFiles | Where-Object { ($newFiles -notcontains $_) -and ($_ -notlike 'tessdata\*') })

            $backup = Join-Path $env:TEMP ('graphite-backup-' + [guid]::NewGuid().ToString('N'))
            $created = New-Object System.Collections.Generic.List[string]
            $ok = $false
            try {
                New-Item -ItemType Directory -Path $backup | Out-Null
                foreach ($rel in ($newFiles + $stale)) {
                    $target = Join-Path $appDir $rel
                    if (Test-Path -LiteralPath $target -PathType Leaf) {
                        $dest = Join-Path $backup $rel
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $dest) | Out-Null
                        Copy-Item -LiteralPath $target -Destination $dest -Force
                    }
                }
                foreach ($rel in $newFiles) {
                    $src = Join-Path $stage $rel
                    $target = Join-Path $appDir $rel
                    if (-not (Test-Path -LiteralPath $target)) { $created.Add($rel) }
                    New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) | Out-Null
                    $tries = 0
                    while ($true) {
                        try { Copy-Item -LiteralPath $src -Destination $target -Force; break }
                        catch { $tries++; if ($tries -ge 20) { throw }; Start-Sleep -Milliseconds 250 }
                    }
                }
                foreach ($rel in $stale) { Remove-Item -LiteralPath (Join-Path $appDir $rel) -Force -ErrorAction SilentlyContinue }
                $newFiles | Set-Content -LiteralPath $manifest -Encoding UTF8
                $ok = $true
                Log 'Updated to {{Ps(tag)}}'
            }
            catch {
                $reason = "$_"
                Log ('Update to {{Ps(tag)}} failed, restoring the previous version: ' + $reason)
                foreach ($rel in $created) { Remove-Item -LiteralPath (Join-Path $appDir $rel) -Force -ErrorAction SilentlyContinue }
                if (Test-Path -LiteralPath $backup) {
                    $backupRoot = (Get-Item -LiteralPath $backup).FullName.TrimEnd('\')
                    Get-ChildItem -LiteralPath $backup -Recurse -File | ForEach-Object {
                        $rel = $_.FullName.Substring($backupRoot.Length).TrimStart('\')
                        $target = Join-Path $appDir $rel
                        New-Item -ItemType Directory -Force -Path (Split-Path -Parent $target) -ErrorAction SilentlyContinue | Out-Null
                        Copy-Item -LiteralPath $_.FullName -Destination $target -Force -ErrorAction SilentlyContinue
                    }
                }
                try { Set-Content -LiteralPath $failNote -Value ('Graphite {{Ps(tag)}} could not be installed (' + $reason + '). The previous version was kept.') } catch { }
            }

            Start-Process -FilePath $exe
            Remove-Item -LiteralPath $backup -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $stage -Recurse -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $zip -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath $MyInvocation.MyCommand.Path -Force -ErrorAction SilentlyContinue
            """;
    }

    /// <summary>Opens the release page in the default browser (fallback when the
    /// in-place update can't run, e.g. no zip asset or an unwritable install dir).</summary>
    public static void OpenReleasePage(UpdateInfo info) =>
        Process.Start(new ProcessStartInfo(info.ReleaseUrl) { UseShellExecute = true });
}
