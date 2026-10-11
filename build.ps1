# Builds the App Volume Mixer plugin into artifacts\*.macroDeckPlugin
# Usage (PowerShell):  .\build.ps1
$ErrorActionPreference = 'Stop'
$cliVersion = '3.0.0-beta.16'
$toolsDir = Join-Path $env:USERPROFILE '.dotnet\tools'
if ($env:PATH -notlike "*$toolsDir*") { $env:PATH = "$env:PATH;$toolsDir" }

if (-not (Get-Command macrodeck-plugin -ErrorAction SilentlyContinue)) {
    Write-Host "Installing the Macro Deck plugin CLI ($cliVersion)..."
    dotnet tool install --global MacroDeck.Plugin.Cli --version $cliVersion
}

$project = Join-Path $PSScriptRoot 'src\AppVolumeMixer'
$artifacts = Join-Path $PSScriptRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $artifacts | Out-Null

Push-Location $project
try {
    macrodeck-plugin build --output $artifacts --force
    if ($LASTEXITCODE -ne 0) { throw "macrodeck-plugin build failed ($LASTEXITCODE)" }
    $pkg = Get-ChildItem $artifacts -Filter *.macroDeckPlugin | Sort-Object LastWriteTime | Select-Object -Last 1
    macrodeck-plugin validate --artifact $pkg.FullName
    Write-Host ""
    Write-Host "Done: $($pkg.FullName)" -ForegroundColor Green
    Write-Host "Install it in Macro Deck: Integrations > Install from file..."
}
finally {
    Pop-Location
}
