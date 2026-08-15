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
#   ./Publish-Tally.ps1 -ShowAllOutput   # unfiltered build output, for diagnosing a failure
#
# A clean run prints its step headings and nothing else: the build tools' routine narration is
# filtered out so that anything left on screen is worth reading. Warnings and errors always show.
#
# The token is never stored in the repo or the app, only in an encrypted per-user file outside the
# repo. After this runs, installed apps pick up the new version automatically within a launch or two.
# For anyone NOT yet on an auto-updating build, hand them dist/Tally-win-Setup.exe from this run once.
[CmdletBinding()]
param(
    [string] $Version,                     # blank = auto-bump the patch from the latest release
    [string] $Token = $env:GITHUB_TOKEN,   # blank = use the saved token, or prompt once and save it
    [string] $Runtime = 'win-x64',
    [switch] $ShowAllOutput                # print every line the build tools emit, unfiltered
)

$ErrorActionPreference = 'Stop'
# Native tools write progress to stderr as a matter of course; without this, PowerShell 7 can treat
# a routine line as a terminating error the moment it is redirected into the pipeline for filtering.
$PSNativeCommandUseErrorActionPreference = $false
$repo = 'https://github.com/Zac-Lutz/Tally'

# Velopack narrates everything it does, and two of its warnings are simply descriptions of how we
# publish rather than problems: we hold no code-signing certificate, so it says so once per bundle
# and draws a Code-sign bar that necessarily sits at 0%. Nothing there is actionable — the only
# consequence is a SmartScreen prompt on a FRESH install, which the README explains — and printing
# it every time trains the eye to ignore the window where a real warning would appear.
#
# So this list is deliberately narrow: it names lines known to mean nothing, and EVERYTHING else
# still prints. If a signing certificate is ever configured, a genuine signing failure reads
# differently and comes straight through.
$script:BenignOutput = @(
    'No signing parameters provided'   # no certificate configured; see "Auto-update" in README.md
    'Code-sign application'            # that step's progress bar, therefore always 0%
)

# True for output a person should read: not blank, not known-benign, not routine chatter.
function Test-WorthShowing([string] $line) {
    if ($ShowAllOutput) { return $true }
    if ([string]::IsNullOrWhiteSpace($line)) { return $false }
    foreach ($benign in $script:BenignOutput) {
        if ($line -like "*$benign*") { return $false }
    }

    # A warning or error always survives, whatever else it looks like. Velopack stamps the level
    # inside the timestamp — "[09:15:56 WRN] ..." — so the bracket is not next to the level.
    if ($line -match '\s(WRN|ERR|FTL)\]') { return $true }

    # Routine narration and finished progress bars say only "still working", which the script's
    # own step messages already say more clearly.
    if ($line -match '\sINF\]') { return $false }
    if ($line -match '-{5,}\s+\d+%') { return $false }
    return $true
}

# Runs a build tool, showing only what's worth reading, and stops the publish if it fails.
function Invoke-BuildStep {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [Parameter(Mandatory)] [string[]] $Arguments,
        [Parameter(Mandatory)] [string] $FailureMessage,
        [switch] $AllowFailure             # the step is an optimization; publishing survives without it
    )

    & $Exe @Arguments 2>&1 | ForEach-Object {
        $line = "$_"
        if (Test-WorthShowing $line) { Write-Host $line }
    }

    if ($LASTEXITCODE -ne 0 -and -not $AllowFailure) { throw $FailureMessage }
}

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
Invoke-BuildStep $dotnet @(
    'publish', (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj'),
    '-c', 'Release', '-r', $Runtime, '--self-contained', '-o', $publish,
    '--nologo', '-v', 'quiet'
) 'dotnet publish failed'

# Pull existing releases so vpk can build small delta updates. Empty/no-op on the first publish —
# and a failure here only costs users a bigger download, so it must never stop the publish.
Write-Host 'Fetching existing releases (for delta updates)...'
Invoke-BuildStep $vpk @(
    'download', 'github', '--repoUrl', $repo, '--token', $Token, '--outputDir', $dist
) 'vpk download failed' -AllowFailure

Write-Host "Packing v$Version..."
Invoke-BuildStep $vpk @(
    'pack',
    '--packId', 'Tally',
    '--packTitle', 'Tally',
    '--packAuthors', 'Tally',
    '--packVersion', $Version,
    '--packDir', $publish,
    '--mainExe', 'tally.exe',
    '--icon', (Join-Path $PSScriptRoot 'src/Tally.App/Assets/tally.ico'),
    '--outputDir', $dist
) 'vpk pack failed'

Write-Host 'Uploading and publishing the GitHub release...'
Invoke-BuildStep $vpk @(
    'upload', 'github',
    '--repoUrl', $repo,
    '--token', $Token,
    '--publish',
    '--releaseName', "Tally $Version",
    '--tag', $Version,
    '--outputDir', $dist
) 'vpk upload failed'

Write-Host ''
Write-Host "Published Tally $Version to $repo/releases."
Write-Host 'Installed apps update themselves automatically. First-timers: hand them dist\Tally-win-Setup.exe once.'
