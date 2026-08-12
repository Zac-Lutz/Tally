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

The app lives in the system tray as a tally-marks icon that shows its state at a glance:
**green** while tracking, **red** when paused. Right-click it for:

- **Open live view** — a dashboard window that shows the current day and refreshes in place
- **Pause/Resume tracking**
- **Generate today's / yesterday's report** — writes and opens a snapshot
- **Open reports / data folder**
- **Exit**

## Live view

**Open live view** (tray menu, or `tally.exe --live`) opens an in-app window showing the same
rollup / calls / timeline / activity as a report, for **today**, refreshing every ~5 seconds
so you can watch it fill in without generating anything. It renders through the same
`HtmlReportWriter` as the file report, so the live view and a snapshot always agree. A
**Generate snapshot report** button on its toolbar writes a timestamped report when you want a
frozen copy. The window uses the Microsoft Edge WebView2 runtime (preinstalled on Windows 11);
if it's missing, the window says so and reports still work from the tray.

## Manual timers

Alongside the automatic tracking you can run **manual timers** — a named span you start and stop
yourself (e.g. "Ticket #123 — phone call"):

- **Global hotkeys** start/stop from anywhere, even when Tally isn't focused. Defaults are
  `Ctrl+Alt+T` (start) and `Ctrl+Alt+S` (stop). **Reconfigure them in the app** — the live
  view's **Hotkeys…** button (or the tray's **Configure hotkeys…**) opens a dialog where you
  press the combo you want; it saves to `settings.json` and rebinds immediately. You can still
  hand-edit `timerStartHotkey` / `timerStopHotkey` in `settings.json` (Ctrl/Alt/Shift/Win + a
  letter or F-key; the in-app picker captures Ctrl/Alt/Shift combos).
- **Name** the timer in the field at the top of the live view; editing while it runs renames it live.
- **Tray menu** has a Start/Stop timer item too.
- When a timer runs and the main window isn't visible (minimized or closed to tray), a small
  **floating bubble** appears at the bottom-right showing the name + elapsed time, with a stop
  button. **Drag it** anywhere; **double-click** it to reopen the app; **right-click** it to
  rename the timer (inline) or stop it — without opening the full app.

Completed timers are saved to the `manual_timers` table. (They're recorded now; surfacing them
inside the report/export is a planned follow-up.)

## Reports: on demand and automatic

Reports can be generated **at any moment** — they cover the day *so far*, so a 2pm report
is a valid picture of the morning. Three triggers:

- **Tray menu** — "Generate today's / yesterday's report" (writes + opens the file).
- **CLI** — `tally.exe --report [today|yesterday|yyyy-MM-dd] [html|md]` writes the file
  headlessly (works while the tray instance runs; usable from a scheduled task). The optional
  format arg overrides the setting for that one run.
- **Automatic daily** — at the time set in `%USERPROFILE%\.tally\settings.json`
  (`autoReportTime`, default `17:30`, machine-local time; `null` disables). Shows a tray
  balloon when ready, or set `openReportOnAutoGenerate: true` to pop the file open. If
  tally starts after that time, it catches up once shortly after startup. Settings are
  read at startup — restart tally (or rerun `Install-Tally.ps1`) after editing.

**Format** — reports render as **HTML by default** (a self-contained, theme-aware page that
opens in the browser: stat cards up top, then **Rollup / Calls / Timeline as tabs** — Rollup
first — over color-coded tables). Set `reportFormat` in `settings.json` to `"markdown"` for
`.md` (stacked sections, no tabs), or `"json"` to emit the machine export directly. The page
is fully self-contained (inline CSS + a little inline JS for the tabs, no external requests),
so it's safe to keep or share as a single file. The live view uses the same tabs and keeps
your selected tab across its refreshes.

**JSON export** — the HTML report has an **Export JSON** button (top-right) that downloads a
`tally-YYYY-MM-DD.json` file built entirely client-side (works offline, no server). The same
data is produced headlessly with `--report today json` or `reportFormat: "json"`. The format
is the `schema_version: "2"` export: a `source`/`range`/`slots` envelope where each slot is a
run of consecutive same-category blocks (bucket = the category slug, hours = summed active
time, plus `window_titles`, `items` from tickets, `machines`, and `evidence` derived from
tickets/Teams chats/overlapping calls). Fields Tally can't populate are honest: `browser` and
`sessions` are always empty (no URL or repo capture), and `summary` is omitted entirely.

**Every run writes its own file** — `yyyy-MM-dd_HHmmss.<ext>` (report date + run time), so
successive runs never overwrite and you can compare a 2pm snapshot with the 5:30pm final.
Each report is recomputed from raw events, so late rule edits apply retroactively. The
output folder is the `reportsDirectory` setting (env vars like `%USERPROFILE%` are
expanded; `null` = `%USERPROFILE%\.tally\reports`) — pointing it at this repo's `reports/`
folder is handy; that path is gitignored.

The tray/app icons are generated by `tools/New-TallyIcon.ps1` into
`src/Tally.App/Assets/` (`tally.ico` green = live, `tally-paused.ico` red = paused); rerun it
to restyle.

## Tests

```powershell
dotnet test tests/Tally.Core.Tests
```

## Classification rules

Edit `%USERPROFILE%\.tally\rules.json` (created with starter rules on first run; comments
allowed). Ordered, first match wins; `processPattern`/`titlePattern` are case-insensitive
regexes; named groups `(?<ticket>...)`, `(?<client>...)`, and `(?<subject>...)` extract those
fields. Rules are re-read on every report generation, so edits apply immediately — check the
report's "Gaps to account for" section for unclassified blocks worth a new rule.

`subject` captures a free-text "what/who" — e.g. the shipped Teams rule pulls the focused
chat/channel name out of `Chat | <name> | Microsoft Teams`, so the rollup lists each
conversation separately instead of one lumped "Teams" row.

The **Rollup** is per-activity, not per-category: each distinct browser tab, editor window,
email, or Teams chat is its own row with time summed across the whole day (so several Halo
ticket tabs open at once each track separately). Halo tickets group by ticket number; Teams
chats by name; everything else by window title, after stripping volatile browser noise
("and N more pages", the trailing browser name) so revisiting a tab rolls up instead of
fragmenting.

## Activity intensity

Each report block carries a **Keys/Clk** count (`412/88` = 412 keystrokes, 88 mouse clicks),
and the summary line totals the day. It's an intensity signal that separates active work
from a window left open — a long block reading `0/0` is a candidate to reclassify or drop at
time-entry. Only **counts** are recorded, sampled once a minute; which keys were pressed is
never observed or stored, so the database can't become a keystroke log.

## Build an installer to give to someone else

```powershell
./Package-Tally.ps1 -Version 1.0.0
```

Produces `dist/Tally-Setup-1.0.0.exe` — a single self-contained installer (via
[Velopack](https://velopack.io)) you can hand to another person. Double-clicking it installs
Tally **per-user with no admin prompt**, bundles the .NET runtime (they don't need .NET
installed), adds Start Menu + Desktop shortcuts and an uninstall entry, and the app
self-registers login autostart. `dist/` also gets a `Tally-win-Portable.zip` (a no-install
run-from-folder build) and the `.nupkg`/`RELEASES` files that enable in-app auto-update if a
release feed is hosted later. Re-run with a higher `-Version` to cut a new release.

The installer is **unsigned**, so Windows SmartScreen shows "Windows protected your PC" on
first run — the recipient clicks **More info -> Run anyway**. For wider distribution, add an
Authenticode code-signing certificate (`vpk pack --signParams ...`) to remove that prompt.

## Install for local development (autostart)

```powershell
./Install-Tally.ps1
```

Publishes a Release build to `%LOCALAPPDATA%\Programs\tally` and starts it; the app
self-registers autostart (HKCU `Run` -> `Tally`) on launch. The installed copy is what
autostarts — dev rebuilds in this repo never conflict with the running instance. Rerun the
script to update the installed copy after changes. Autostart can be turned off with
`"autoStart": false` in `settings.json`, or removed manually:

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
| Settings | `%USERPROFILE%\.tally\settings.json` |
| Reports | `reportsDirectory` setting (default `%USERPROFILE%\.tally\reports\`) |
| Logs | `%USERPROFILE%\.tally\logs\tally.log` |

Design and slice plan: [DESIGN.md](DESIGN.md).
