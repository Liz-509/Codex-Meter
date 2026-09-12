param(
    [string]$Version = "1.4.1"
)

$ErrorActionPreference = "Stop"

$projectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
$project = Join-Path $projectRoot "windows\CodexMeter.Windows\CodexMeter.Windows.csproj"
$installerProject = Join-Path $projectRoot "windows\CodexMeter.Installer\CodexMeter.Installer.csproj"
$publishDirectory = Join-Path $projectRoot "build\windows\win-x64"
$installerBuildDirectory = Join-Path $projectRoot "build\windows\installer"
$installerPublishDirectory = Join-Path $installerBuildDirectory "publish"
$payloadArchive = Join-Path $installerBuildDirectory "Payload.zip"
$outputDirectory = Join-Path $projectRoot "outputs"
$installer = Join-Path $outputDirectory "Codex-Meter-Windows-x64-v$Version.exe"
$legacyInstaller = Join-Path $outputDirectory "Codex-Meter-Windows-x64-v$Version.msi"

if ($Version -notmatch '^\d+\.\d+\.\d+$') {
    throw "Version must contain exactly three numeric parts, for example 1.4.0."
}

if (Test-Path $publishDirectory) {
    Remove-Item $publishDirectory -Recurse -Force
}
if (Test-Path $installerBuildDirectory) {
    Remove-Item $installerBuildDirectory -Recurse -Force
}
New-Item $publishDirectory -ItemType Directory -Force | Out-Null
New-Item $installerBuildDirectory -ItemType Directory -Force | Out-Null
New-Item $outputDirectory -ItemType Directory -Force | Out-Null

dotnet publish $project `
    --configuration Release `
    --runtime win-x64 `
    --self-contained true `
    --output $publishDirectory `
    -p:Version=$Version `
    -p:AssemblyVersion="$Version.0" `
    -p:FileVersion="$Version.0"
if ($LASTEXITCODE -ne 0) {
    throw "Windows publish failed with exit code $LASTEXITCODE"
}

if (-not (Test-Path (Join-Path $publishDirectory "Codex Meter.exe"))) {
    throw "Windows publish did not produce Codex Meter.exe"
}
if (-not (Test-Path (Join-Path $publishDirectory "Resources\companion.html"))) {
    throw "Windows publish did not include companion.html"
}
if (-not (Test-Path (Join-Path $publishDirectory "Resources\usage-widget.js"))) {
    throw "Windows publish did not include usage-widget.js"
}

if (Test-Path $installer) {
    Remove-Item $installer -Force
}
if (Test-Path $legacyInstaller) {
    Remove-Item $legacyInstaller -Force
}

Compress-Archive -Path (Join-Path $publishDirectory "*") -DestinationPath $payloadArchive -CompressionLevel Optimal

$installerBuildArguments = @(
    "publish",
    $installerProject,
    "--configuration", "Release",
    "--runtime", "win-x64",
    "--self-contained", "true",
    "--output", $installerPublishDirectory,
    "-p:PayloadArchive=$payloadArchive",
    "-p:Version=$Version",
    "-p:AssemblyVersion=$Version.0",
    "-p:FileVersion=$Version.0"
)

dotnet @installerBuildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Windows installer build failed with exit code $LASTEXITCODE"
}

$builtInstaller = Join-Path $installerPublishDirectory "Codex Meter Setup.exe"
if (-not (Test-Path $builtInstaller)) {
    throw "Windows installer build did not produce an EXE package"
}

Copy-Item $builtInstaller $installer

Write-Output $installer
