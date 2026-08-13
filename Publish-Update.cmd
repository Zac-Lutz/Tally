@echo off
rem Double-click this to publish a new Tally version. It runs Publish-Tally.ps1, which asks for
rem your GitHub token once (then remembers it, encrypted), auto-picks the next version, and asks
rem you to confirm before publishing. Any arguments you pass are forwarded (e.g. -Version 1.3.0).
setlocal
pushd "%~dp0"
where pwsh >nul 2>nul && (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Tally.ps1" %*
) || (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0Publish-Tally.ps1" %*
)
popd
echo.
pause
