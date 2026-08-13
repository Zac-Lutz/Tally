# Builds a release and publishes it to GitHub Releases so installed apps auto-update.
#
# EASIEST: just double-click Publish-Update.cmd. It calls this with no arguments, which:
#   - reads your saved GitHub token (asks once, then stores it ENCRYPTED for this Windows user),
#   - auto-picks the next version (bumps the patch of the latest published release),
#   - asks you to confirm, then builds + publishes.
#
# Manual use (still supported):
#   ./Publish-Tally.ps1                  # auto-bump the patch (1.2.0 -> 1.2.1)
#   ./Publish-Tally.ps1 -Version 1.3.0   # pin an exact version (for a bigger jump)
#
# The token is never stored in the repo or the app, only in an encrypted per-user file outside the
# repo. After this runs, installed apps pick up the new version automatically within a launch or two.
# For anyone NOT yet on an auto-updating build, hand them dist/Tally-win-Setup.exe from this run once.
[CmdletBinding()]
param(
    [string] $Version,                     # blank = auto-bump the patch from the latest release
    [string] $Token = $env:GITHUB_TOKEN,   # blank = use the saved token, or prompt once and save it
    [string] $Runtime = 'win-x64'
)

$ErrorActionPreference = 'Stop'
$repo = 'https://github.com/Zac-Lutz/Tally'

# Token resolution: an explicit -Token / $env:GITHUB_TOKEN wins; otherwise use the encrypted file
# saved on the first run; otherwise prompt once (masked) and save it encrypted for this user.
# DPAPI (ConvertFrom-SecureString) ties the file to this Windows account, so it is useless if copied
# elsewhere, and it lives under %USERPROFILE%\.tally (outside the repo) so it can never be committed.
function Resolve-PublishToken([string] $provided) {
    if (-not [string]::IsNullOrWhiteSpace($provided)) { return $provided }
    $store = Join-Path $env:USERPROFILE '.tally\publish-token.dat'
    if (Test-Path $store) {
        try {
            $sec = (Get-Content $store -Raw).Trim() | ConvertTo-SecureString
            return [System.Net.NetworkCredential]::new('', $sec).Password
        } catch { Write-Warning 'Saved token could not be read; re-enter it.' }
    }
    Write-Host 'First-time setup: paste your GitHub token (classic token with the "repo" scope).'
    Write-Host 'It is stored ENCRYPTED for your Windows user, so you only enter it this once.'
    $sec = Read-Host 'GitHub token' -AsSecureString
    if (-not $sec -or $sec.Length -eq 0) { throw 'No token entered.' }
    New-Item -ItemType Directory -Force (Split-Path $store) | Out-Null
    $sec | ConvertFrom-SecureString | Set-Content $store
    Write-Host "Token saved (encrypted) to $store"
    return [System.Net.NetworkCredential]::new('', $sec).Password
}

# Next version = the latest published release's patch + 1 (1.2.0 -> 1.2.1). Anonymous read; if there
# are no releases yet, start at 1.0.0. Pass -Version to override for a minor/major jump.
function Get-NextVersion([string] $repoUrl) {
    $api = ($repoUrl -replace 'https://github.com/', 'https://api.github.com/repos/') + '/releases/latest'
    try {
        $tag = (Invoke-RestMethod $api).tag_name
        $p = $tag.TrimStart('v', 'V').Split('.')
        return '{0}.{1}.{2}' -f $p[0], $p[1], ([int]$p[2] + 1)
    } catch {
        return '1.0.0'
    }
}

$Token = Resolve-PublishToken $Token
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = Get-NextVersion $repo }

$reply = Read-Host "Publish Tally $Version to GitHub? [Y/n]"
if ($reply -and $reply -notmatch '^(y|yes)$') { Write-Host 'Cancelled - nothing published.'; exit 0 }

# Some machines have a runtime-only dotnet on PATH ahead of the SDK install; pick one with an SDK.
function Get-SdkDotnet {
    foreach ($c in @('dotnet', (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'), (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))) {
        try { $sdks = & $c --list-sdks 2>$null; if ($LASTEXITCODE -eq 0 -and $sdks) { return $c } } catch { }
    }
    throw 'No .NET SDK found. Install the .NET 10 SDK: https://aka.ms/dotnet/download'
}
$dotnet = Get-SdkDotnet

$vpk = Join-Path $env:USERPROFILE '.dotnet\tools\vpk.exe'
if (-not (Test-Path $vpk)) {
    Write-Host 'Installing the Velopack CLI (vpk)...'
    & $dotnet tool install -g vpk
    if ($LASTEXITCODE -ne 0) { throw 'Failed to install vpk' }
}

$publish = Join-Path $PSScriptRoot 'publish'
$dist = Join-Path $PSScriptRoot 'dist'
Remove-Item $publish -Recurse -Force -ErrorAction SilentlyContinue
Remove-Item $dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $dist | Out-Null

Write-Host "Publishing self-contained ($Runtime)..."
& $dotnet publish (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj') -c Release -r $Runtime --self-contained -o $publish
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
