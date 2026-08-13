# Builds a release and publishes it to GitHub Releases so installed apps auto-update.
#
#   $env:GITHUB_TOKEN = 'ghp_...'      # once per shell (needs repo / Contents: read-write)
#   ./Publish-Tally.ps1 -Version 1.2.0 # must be higher than the last published version
#
# The token is read from the environment and never stored in the repo or the app. After this runs,
# already-installed apps pick up the new version automatically within a launch or two. For anyone
# NOT yet on an auto-updating build, hand them dist/Tally-win-Setup.exe from this run once.
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string] $Version,
    [string] $Token = $env:GITHUB_TOKEN,
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/Zac-Lutz/Tally'

if ([string]::IsNullOrWhiteSpace($Token)) {
    throw "No GitHub token found. Set it in your shell first:  `$env:GITHUB_TOKEN = 'ghp_...'  (needs repo / Contents: read-write). Never commit it."
}

$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) {
    Write-Host 'Installing the Velopack CLI (vpk)...'
    dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install vpk' }
}

$publish = Join-Path $PSScriptRoot 'publish'
$dist = Join-Path $PSScriptRoot 'dist'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Publishing self-contained ($Runtime)..."
dotnet publish (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj') -c Release -r $Runtime --self-contained -o $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Pull existing releases so vpk can build small delta updates. Empty/no-op on the first publish.
Write-Host 'Fetching existing releases (for delta updates)...'
& $vpk download github --repoUrl $repo --token $Token --outputDir $dist 2>&1 | Out-Host

Write-Host "Packing v$Version..."
& $vpk pack `
    --packId Tally `
    --packTitle 'Tally' `
    --packAuthors 'Tally' `
    --packVersion $Version `
    --packDir $publish `
    --mainExe 'tally.exe' `
    --icon (Join-Path $PSScriptRoot 'src/Tally.App/Assets/tally.ico') `
    --outputDir $dist
if ($LASTEXITCODE -ne 0) { throw 'vpk pack failed' }

Write-Host 'Uploading and publishing the GitHub release...'
& $vpk upload github `
    --repoUrl $repo `
    --token $Token `
    --publish `
    --releaseName "Tally $Version" `
    --tag $Version `
    --outputDir $dist
if ($LASTEXITCODE -ne 0) { throw 'vpk upload failed' }

Write-Host ''
Write-Host "Published Tally $Version to $repo/releases."
Write-Host 'Installed apps update themselves automatically. First-timers: hand them dist\Tally-win-Setup.exe once.'
