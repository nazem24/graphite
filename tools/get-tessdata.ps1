# Downloads Tesseract OCR language data used by Graphite's OCR feature.
# Run once from the repository root (or anywhere):  powershell -File tools\get-tessdata.ps1
param(
    [string[]]$Languages = @("eng")
)

$target = Join-Path $PSScriptRoot "..\src\Graphite.App\tessdata"
New-Item -ItemType Directory -Force -Path $target | Out-Null

foreach ($lang in $Languages) {
    $url = "https://github.com/tesseract-ocr/tessdata_fast/raw/main/$lang.traineddata"
    $out = Join-Path $target "$lang.traineddata"
    Write-Host "Downloading $lang.traineddata …"
    Invoke-WebRequest -Uri $url -OutFile $out
}

Write-Host "Done. Rebuild Graphite so the tessdata folder is copied next to the executable."
