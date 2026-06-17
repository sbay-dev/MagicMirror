#requires -Version 5.1
[CmdletBinding()]
param(
    [string]$Root = (Split-Path -Parent $PSScriptRoot)
)

function Invoke-EcCommand {
    param(
        [Parameter(Mandatory = $true)][string]$Command,
        [Parameter(Mandatory = $true)][string]$CouncilRoot
    )

    $ec = Get-Command ec.cmd -ErrorAction SilentlyContinue
    if (-not $ec) {
        $ec = Get-Command ec -ErrorAction SilentlyContinue
    }

    if ($ec) {
        & $ec.Source "--$Command" '--council-root' $CouncilRoot
        exit $LASTEXITCODE
    }

    $candidate = $env:EC_APP
    if ([string]::IsNullOrWhiteSpace($candidate)) {
        $sessionPath = Join-Path $CouncilRoot 'session.json'
        if (Test-Path -LiteralPath $sessionPath) {
            $session = Get-Content -LiteralPath $sessionPath -Raw | ConvertFrom-Json
            $candidate = $session.app_path
        }
    }

    if (-not [string]::IsNullOrWhiteSpace($candidate) -and (Test-Path -LiteralPath $candidate)) {
        & $candidate $Command -CouncilRoot $CouncilRoot
        exit $LASTEXITCODE
    }

    throw 'Cannot locate ec CLI. Install it with install-ec.ps1, add ec.cmd to PATH, or set EC_APP to app\council.ps1.'
}
Invoke-EcCommand -Command tally -CouncilRoot $Root