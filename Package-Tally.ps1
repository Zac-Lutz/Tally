# Builds a distributable installer (Setup.exe) with Velopack.
#
#   ./Package-Tally.ps1 -Version 1.0.0
#
# Output: dist/Setup.exe  — a single per-user installer to hand to someone else. It bundles the
# .NET runtime (self-contained), installs without admin, adds Start Menu + uninstall entries, and
# the app self-registers login autostart. Re-run with a higher -Version to cut a new release; if a
# release feed is hosted later, the same output enables in-app auto-update.
[CmdletBinding()]
param(
    [string] $Version = '1.0.0',
    [string] $OutDir = (Join-Path $PSScriptRoot 'dist'),
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'

# Velopack's CLI (vpk) is a global dotnet tool. Install on first run.
$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) {
    Write-Host 'Installing the Velopack CLI (vpk)...'
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install vpk' }
}

$publish = Join-Path $PSScriptRoot 'publish'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue

# Start from a clean output dir so re-running the same version always rebuilds. (Velopack treats
# the output dir as a release feed and refuses to re-pack an existing version. If you later host
# an auto-update feed, keep prior releases here instead and bump -Version each time.)
Remove-Item $OutDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Publishing self-contained ($Runtime)..."
dotnet publish (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj') `
    -c Release -r $Runtime --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

Write-Host "Packing Setup.exe (v$Version)..."
& $vpk pack `
    --packId Tally `
    --packTitle 'Tally' `
    --packAuthors 'Tally' `
    --packVersion $Version `
    --packDir $publish `
    --mainExe 'tally.exe' `
    --icon (Join-Path $PSScriptRoot 'src/Tally.App/Assets/tally.ico') `
    --outputDir $OutDir
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

# Velopack names the bundle "Tally-win-Setup.exe"; copy to a cleaner handoff name.
$setup = Join-Path $OutDir 'Tally-win-Setup.exe'
$friendly = Join-Path $OutDir "Tally-Setup-$Version.exe"
if (Test-Path $setup) { Copy-Item $setup $friendly -Force }

Write-Host ''
Write-Host "Installer ready: $friendly"
Write-Host 'Hand that file to the other person; double-clicking installs Tally for their user (no admin).'
Write-Host 'Note: it is unsigned, so SmartScreen shows "More info -> Run anyway" on first launch.'
