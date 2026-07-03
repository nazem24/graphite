# Registers Graphite as an available handler for .pdf files (per-user, no admin needed).
# Usage:  powershell -File tools\register-file-association.ps1 -ExePath "C:\path\to\Graphite.exe"
# After running, pick Graphite in Settings > Apps > Default apps > .pdf
# (Windows protects the default-app choice itself, so that last click must be yours.)
param(
    [Parameter(Mandatory = $true)]
    [string]$ExePath
)

if (-not (Test-Path $ExePath)) { throw "Executable not found: $ExePath" }
$ExePath = (Resolve-Path $ExePath).Path

$classes = "HKCU:\Software\Classes"
$progId  = "Graphite.PDF"

New-Item -Path "$classes\$progId" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId" -Name "(default)" -Value "PDF Document (Graphite)"
New-Item -Path "$classes\$progId\DefaultIcon" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId\DefaultIcon" -Name "(default)" -Value "`"$ExePath`",0"
New-Item -Path "$classes\$progId\shell\open\command" -Force | Out-Null
Set-ItemProperty -Path "$classes\$progId\shell\open\command" -Name "(default)" -Value "`"$ExePath`" `"%1`""

New-Item -Path "$classes\.pdf\OpenWithProgids" -Force | Out-Null
New-ItemProperty -Path "$classes\.pdf\OpenWithProgids" -Name $progId -PropertyType None -Value ([byte[]]@()) -Force | Out-Null

# Register under App Paths so "Open with" finds it by name.
$appPaths = "HKCU:\Software\Microsoft\Windows\CurrentVersion\App Paths\Graphite.exe"
New-Item -Path $appPaths -Force | Out-Null
Set-ItemProperty -Path $appPaths -Name "(default)" -Value $ExePath

Write-Host "Registered. To make Graphite the default PDF viewer:"
Write-Host "  Settings > Apps > Default apps > choose defaults by file type > .pdf > Graphite"
