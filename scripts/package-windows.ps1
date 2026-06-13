param(
    [string]$Version = "1.0.4",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$artifacts = Join-Path $root "artifacts"
$publish = Join-Path $artifacts "windows-x64"
$appDir = Join-Path $publish "MagicMirror"
$zip = Join-Path $artifacts "MagicMirror-v$Version-windows-x64.zip"

Set-Location $root

if (Test-Path $publish) { Remove-Item $publish -Recurse -Force }
New-Item -ItemType Directory -Force -Path $appDir | Out-Null

Write-Host "==> Publishing MagicMirror.Native"
dotnet publish ".\MagicMirror.Native\MagicMirror.Native.csproj" `
    -f net10.0-windows10.0.19041.0 `
    -c $Configuration `
    -r win-x64 `
    --self-contained false `
    --nologo `
    -v minimal `
    -p:PublishReadyToRun=false `
    -o $appDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

@"
Magic Mirror v$Version
======================

Run:
  MagicMirror.Native.exe

Requirements:
  - Windows 10 1809 or newer
  - .NET 10 runtime/SDK for this framework-dependent package
  - Optional Windows OCR language packs
  - Optional local Tesseract installation for eng+ara OCR

Documentation:
  https://github.com/sbay-dev/MagicMirror
"@ | Set-Content -Path (Join-Path $appDir "README-RUN.txt") -Encoding UTF8

@"
@echo off
setlocal
cd /d "%~dp0"
start "" "MagicMirror.Native.exe"
"@ | Set-Content -Path (Join-Path $appDir "run-magic-mirror.cmd") -Encoding ASCII

if (Test-Path $zip) { Remove-Item $zip -Force }
Compress-Archive -Path (Join-Path $appDir "*") -DestinationPath $zip -Force

Write-Host "==> Created $zip"
