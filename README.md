# Tally

Local-only Windows activity tracker for end-of-day time entry. Runs in the system tray,
records foreground windows (including browser tab switches via title changes), calls
(mic-in-use detection), and idle/lock time — then generates a markdown report of your day.

All data stays in `%USERPROFILE%\.tally\`. Nothing leaves the machine.

## Build & run

```powershell
dotnet build Tally.slnx
dotnet run --project src/Tally.App
```

The app lives in the system tray (no window). Right-click the icon for:

- **Pause/Resume tracking**
- **Generate today's / yesterday's report** — writes and opens
  `%USERPROFILE%\.tally\reports\YYYY-MM-DD.md`
- **Open reports / data folder**
- **Exit**

## Tests

```powershell
dotnet test tests/Tally.Core.Tests
```

## Classification rules

Edit `%USERPROFILE%\.tally\rules.json` (created with starter rules on first run; comments
allowed). Ordered, first match wins; `processPattern`/`titlePattern` are case-insensitive
regexes; named groups `(?<ticket>...)` and `(?<client>...)` extract those fields. Rules are
re-read on every report generation, so edits apply immediately — check the report's
"Gaps to account for" section for unclassified blocks worth a new rule.

## Install (autostart)

```powershell
./Install-Tally.ps1
```

Publishes a Release build to `%LOCALAPPDATA%\Programs\tally`, registers autostart via the
HKCU `Run` key (`Tally`), and starts it. The installed copy is what autostarts — dev
rebuilds in this repo never conflict with the running instance. Rerun the script to update
the installed copy after changes. To uninstall autostart:

```powershell
Remove-ItemProperty -Path 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Run' -Name 'Tally'
```

Headless report (e.g. from a scheduled task): `tally.exe --report [today|yesterday|yyyy-MM-dd]`
writes the file without opening it.

## Data locations

| What | Where |
|---|---|
| Event database | `%USERPROFILE%\.tally\tally.db` |
| Classification rules | `%USERPROFILE%\.tally\rules.json` |
| Reports | `%USERPROFILE%\.tally\reports\` |
| Logs | `%USERPROFILE%\.tally\logs\tally.log` |

Design and slice plan: [DESIGN.md](DESIGN.md).
