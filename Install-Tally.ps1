# Publishes tally to a stable per-user location, registers autostart, and (re)starts it.
# Rerun after pulling changes to update the installed copy.
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\tally')
)

$ErrorActionPreference = 'Stop'

# Some machines have a runtime-only dotnet on PATH ahead of the SDK install; pick one with an SDK.
function Get-SdkDotnet {
    foreach ($c in @('dotnet', (Join-Path $env:LOCALAPPDATA 'Microsoft\dotnet\dotnet.exe'), (Join-Path $env:ProgramFiles 'dotnet\dotnet.exe'))) {
        try { $sdks = & $c --list-sdks 2>$null; if ($LASTEXITCODE -eq 0 -and $sdks) { return $c } } catch { }
    }
    throw 'No .NET SDK found. Install the .NET 10 SDK: https://aka.ms/dotnet/download'
}
$dotnet = Get-SdkDotnet

# Stop a running instance first so the install-dir exe isn't locked during publish.
# Hard stop is acceptable: the recorder writes events in small prompt batches.
$running = Get-Process -Name tally -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running tally instance...'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host "Publishing tally (Release) to $InstallDir..."
& $dotnet publish (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj') -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Autostart is registered by the app itself on launch (see Autostart.cs), pointing the HKCU
# Run entry at whichever exe is running — so starting the published copy below sets it up.
$exePath = Join-Path $InstallDir 'tally.exe'
Start-Process -FilePath $exePath
Write-Host 'tally is running from the installed copy (autostart self-registers on launch).'
