# Graphite Add-Text white-screen repro driver (temporary diagnostic tooling)
# Source of truth = %TEMP%\graphite-diag.log written by the app itself.
param(
    [string]$Exe = "C:\Users\micha\Documents\Claude Cowork Area\Cowork PDF viewer\src\Graphite.App\bin\Debug\net8.0-windows\Graphite.exe",
    [string]$Pdf = "C:\Users\micha\Documents\Claude Cowork Area\Cowork PDF viewer\test-large.pdf",
    [string]$OutDir = "C:\Users\micha\Documents\Claude Cowork Area\Cowork PDF viewer\repro-out",
    [string]$Tag = "run"
)

$ErrorActionPreference = 'Stop'
New-Item -ItemType Directory -Force -Path $OutDir | Out-Null

Add-Type -AssemblyName UIAutomationClient, UIAutomationTypes, System.Drawing, System.Windows.Forms
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class Win32 {
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] public static extern bool SetCursorPos(int X, int Y);
    [DllImport("user32.dll")] public static extern void mouse_event(uint dwFlags, uint dx, uint dy, int dwData, UIntPtr dwExtraInfo);
    [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
}
"@
[Win32]::SetProcessDPIAware() | Out-Null

function Assert-Foreground($hwnd) {
    for ($i = 0; $i -lt 10; $i++) {
        [Win32]::SetForegroundWindow($hwnd) | Out-Null
        Start-Sleep -Milliseconds 150
        if ([Win32]::GetForegroundWindow() -eq $hwnd) { return $true }
    }
    return $false
}

function Capture-Window($hwnd, $path) {
    if (-not (Assert-Foreground $hwnd)) { Write-Output "WARN: not foreground for capture $path" }
    $r = New-Object Win32+RECT
    [Win32]::GetWindowRect($hwnd, [ref]$r) | Out-Null
    $w = $r.Right - $r.Left; $h = $r.Bottom - $r.Top
    $bmp = New-Object System.Drawing.Bitmap $w, $h
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.CopyFromScreen($r.Left, $r.Top, 0, 0, $bmp.Size)
    $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $g.Dispose(); $bmp.Dispose()
}

function Click-At($x, $y) {
    [Win32]::SetCursorPos($x, $y) | Out-Null
    Start-Sleep -Milliseconds 120
    [Win32]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [Win32]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero)
}

function Find-ElementRetry($root, $cond, [int]$tries = 10) {
    for ($i = 0; $i -lt $tries; $i++) {
        $el = $root.FindFirst([System.Windows.Automation.TreeScope]::Descendants, $cond)
        if ($null -ne $el) { return $el }
        Start-Sleep -Milliseconds 500
    }
    return $null
}

# --- launch ---------------------------------------------------------------
Remove-Item "$env:TEMP\graphite-diag.log" -ErrorAction SilentlyContinue
$env:GRAPHITE_DEBUG_GOTO = '11'   # jump to page 12 shortly after load (in-app hook)
$proc = Start-Process -FilePath $Exe -ArgumentList "`"$Pdf`"" -PassThru
Remove-Item Env:GRAPHITE_DEBUG_GOTO
Write-Output "PID: $($proc.Id)"

$hwnd = [IntPtr]::Zero
for ($i = 0; $i -lt 40; $i++) {
    Start-Sleep -Milliseconds 500
    $proc.Refresh()
    if ($proc.MainWindowHandle -ne [IntPtr]::Zero) { $hwnd = $proc.MainWindowHandle; break }
}
if ($hwnd -eq [IntPtr]::Zero) { Write-Output "ERROR: no main window"; $proc.Kill(); exit 1 }

[Win32]::ShowWindow($hwnd, 3) | Out-Null   # SW_MAXIMIZE
Assert-Foreground $hwnd | Out-Null
Start-Sleep -Seconds 4

$root = [System.Windows.Automation.AutomationElement]::FromHandle($hwnd)
$rect = $root.Current.BoundingRectangle

# --- wait for the in-app debug hook to jump to page 12 -----------------------
Start-Sleep -Seconds 6
Assert-Foreground $hwnd | Out-Null
Capture-Window $hwnd "$OutDir\$Tag-1-page12.png"

# --- select the Add Text tool ----------------------------------------------
$textTool = Find-ElementRetry $root (New-Object System.Windows.Automation.AndCondition(
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::ControlTypeProperty, [System.Windows.Automation.ControlType]::RadioButton)),
    (New-Object System.Windows.Automation.PropertyCondition([System.Windows.Automation.AutomationElement]::HelpTextProperty, 'Add text'))))
if ($null -eq $textTool) { Write-Output "ERROR: 'Add text' radio not found"; $proc.Kill(); exit 1 }
$textTool.GetCurrentPattern([System.Windows.Automation.SelectionItemPattern]::Pattern).Select()
Start-Sleep -Milliseconds 500

# --- click on the page, then type ------------------------------------------
Assert-Foreground $hwnd | Out-Null
$cx = [int]($rect.Left + $rect.Width * 0.5)
$cy = [int]($rect.Top + $rect.Height * 0.5)
Click-At $cx $cy
Start-Sleep -Milliseconds 800
Capture-Window $hwnd "$OutDir\$Tag-2-editor-open.png"

Assert-Foreground $hwnd | Out-Null
[System.Windows.Forms.SendKeys]::SendWait('hello world')
Start-Sleep -Milliseconds 1500
Capture-Window $hwnd "$OutDir\$Tag-3-after-typing.png"

Start-Sleep -Seconds 1
$proc.Kill()
Write-Output '--- diag log ---'
Get-Content "$env:TEMP\graphite-diag.log" -ErrorAction SilentlyContinue
Write-Output '--- end ---'
