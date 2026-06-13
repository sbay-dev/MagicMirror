param(
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Label,
        [Parameter(Mandatory = $true)]
        [scriptblock]$Command
    )

    Write-Host "==> $Label"
    & $Command
    if ($LASTEXITCODE -ne 0) {
        throw "$Label failed with exit code $LASTEXITCODE"
    }
}

Invoke-Checked "Building Cepha web layer" {
    dotnet build ".\MagicMirror\MagicMirror.csproj" --nologo -v minimal -c $Configuration
}

Invoke-Checked "Building MAUI native layer" {
    dotnet build ".\MagicMirror.Native\MagicMirror.Native.csproj" `
        -f net10.0-windows10.0.19041.0 `
        --nologo -v minimal -c $Configuration `
        -p:PublishReadyToRun=false
}

Write-Host "==> Verification complete"
