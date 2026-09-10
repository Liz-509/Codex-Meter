param(
    [string]$Version = "1.1.2"
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $projectRoot "windows\CodexMeter.Windows\CodexMeter.Windows.csproj"
$publishDirectory = Join-Path $projectRoot "build\windows\win-x64"
$outputDirectory = Join-Path $projectRoot "outputs"
$archive = Join-Path $outputDirectory "Codex-Meter-Windows-x64-v$Version.zip"

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}
New-Item $publishDirectory -ItemType Directory -Force | Out-Null
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0"

if (-not (Test-Path (Join-Path $publishDirectory "Codex Meter.exe"))) {
    throw "Windows publish did not produce Codex Meter.exe"
}
if (-not (Test-Path (Join-Path $publishDirectory "Resources\companion.html"))) {
    throw "Windows publish did not include companion.html"
}
if (-not (Test-Path (Join-Path $publishDirectory "Resources\usage-widget.js"))) {
    throw "Windows publish did not include usage-widget.js"
}

if (Test-Path $archive) {
    Remove-Item $archive -Force
}
Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $archive -CompressionLevel Optimal

Write-Output $archive
