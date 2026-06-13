param(
    [string]$GatewayPath = "cloudflare\magicmirror-sarmad-gateway"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$fullPath = Join-Path $root $GatewayPath

if (-not (Test-Path (Join-Path $fullPath "wrangler.toml"))) {
    throw "Could not find wrangler.toml under $fullPath"
}

Push-Location $fullPath
try {
    wrangler deploy
}
finally {
    Pop-Location
}
