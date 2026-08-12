# Publishes tally to a stable per-user location, registers autostart, and (re)starts it.
# Rerun after pulling changes to update the installed copy.
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\tally')
)

$ErrorActionPreference = 'Stop'

# Stop a running instance first so the install-dir exe isn't locked during publish.
# Hard stop is acceptable: the recorder writes events in small prompt batches.
$running = Get-Process -Name tally -ErrorAction SilentlyContinue
if ($running) {
    Write-Host 'Stopping running tally instance...'
    $running | Stop-Process -Force
    Start-Sleep -Seconds 2
}

Write-Host "Publishing tally (Release) to $InstallDir..."
dotnet publish (Join-Path $PSScriptRoot 'src/Tally.App/Tally.App.csproj') -c Release -o $InstallDir
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

$exePath = Join-Path $InstallDir 'tally.exe'
Set-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' `
    -Name 'Tally' -Value ('"{0}"' -f $exePath)
Write-Host 'Autostart registered (HKCU Run "Tally").'

Start-Process -FilePath $exePath
Write-Host 'tally is running from the installed copy.'
