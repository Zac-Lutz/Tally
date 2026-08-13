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
**green** while tracking, **red** when paused. **Left-click** it to open the live view;
**right-click** for:

- **Open live view** — a dashboard window that shows the current day and refreshes in place
- **Pause/Resume tracking**
- **Generate today's / yesterday's report** — writes and opens a snapshot
- **Settings** — hotkeys and auto-report times
- **Check for updates** — pull and apply the newest version right now (see Auto-update below)
- **Open reports / data folder**
- **Exit**

## Live view

**Open live view** (tray menu, left-click the tray icon, or `tally.exe --live`) opens an in-app
window showing the same rollup / timesheet / timeline / calls / timers / unclassified as a report, for
**today**, refreshing every ~5 seconds so you can watch it fill in without generating anything. In the **Rollup**, the
**Ticket** column is editable: click any row's Ticket cell — a window activity *or a call* — and
type a number. It's saved for that day (in `ticket-overrides.json`) and shows on the Rollup and in
your generated reports (activity tickets also flow into the JSON export).
It renders through the same `HtmlReportWriter` as the file report, so the live view and a snapshot
always agree. A
**Generate snapshot report** button on its toolbar writes a timestamped report when you want a
frozen copy. The window uses the Microsoft Edge WebView2 runtime (preinstalled on Windows 11);
if it's missing, the window says so and reports still work from the tray.

## Timesheet export

At the end of the day, **Export timesheet** in the live view writes a `.json` file you upload to
the att timesheet (Time Entry → the calendar panel → **Import suggestions**). Each entry becomes a
suggestion card sitting next to your meetings; click **Log Time** on one and the dialog opens
prefilled. Nothing is entered automatically, re-uploading never touches entries you've already
created, and once you log time from a suggestion it shows **Added**.

**Export part of a day.** Clicking **Export timesheet** asks how much of the day to cover.
**Everything so far** is the default; untick it to set a From and To — file the morning at lunch,
then the afternoon at the end of the day. The entry count and hours update as you change the
times, so you're choosing against what it will actually produce, and the window lands in the
filename (`tally-2026-08-13-0800-1200.json`).

An entry belongs to the window it *started* in, so two slices never double-count a meeting that
ran through the cut-off. One thing to know: importing into att **replaces that day's suggestions**,
so log the morning's entries before you upload the afternoon — time you've already logged is never
touched, but un-logged suggestion cards from the earlier slice will go.

**Check it before you send it.** The **Timesheet** tab always shows the whole day, drawn as a calendar — each entry a
block sitting where the work happened, coloured by category, with the hours to enter on it. Hover a
block for its times and the measured-vs-rounded figures. Entries that overlap in time sit side by
side, and unaccounted time shows as empty space, so a gap in your day is something you can see
rather than something you have to work out. The solid part of a block is the time actually
measured: a block that's mostly faint spanned an hour but only earned a few minutes of it. The
Export button brings this tab up first.

### How a day becomes a handful of entries

The unit is what you'd **bill it to** — a ticket if one was detected, otherwise the category —
within one working session. A ticket worked morning and afternoon is two entries sitting where the
work happened, not one card smeared across the day.

**Time is only ever counted once**, in priority order:

1. **A manual timer wins.** You started it deliberately, so it's the clearest statement of what
   that time was.
2. **Then a meeting.** An hour on a call is an hour of meeting even though you were reading a
   ticket through it — otherwise meetings dissolve into whatever happened to be on screen. What
   *was* on screen is kept as the entry's detail.
3. **Then window activity**, for whatever's left.

**Nothing is dropped for being short**, because ten two-minute visits are twenty real minutes.
Anything under five minutes gets two rescues: leftovers are re-pooled per ticket across the day
(six two-minute visits to one ticket become one twelve-minute entry), and whatever still can't
stand alone is combined into a single **odds and ends** entry that carries all its detail. That
row is highlighted — it's the one that needs a human.

Hours round to the nearest five minutes and never to zero, so a short activity reports something.

### The one thing you still do by hand

If you already logged time against a ticket directly in Halo, Tally doesn't know. Its suggestion
for that ticket will still appear. Both show on the same calendar, so you can see it — but that
reconciliation is yours to make. Tally is deliberately offline and never talks to Halo.

### Format details

The file is the Suggestion Export `schema_version: "2"`: a `source`/`range`/`slots` envelope.
Every bound the importer enforces is enforced when writing (unique ids, hours above zero, capped
titles and evidence), so a rejected upload should mean a real bug, not a long day. Fields Tally
can't populate are honest: `browser` and `sessions` are always empty (no URL or repo capture).
`summary` is supplied only where it helps — a ticketed entry omits it so the ticket composes the
note, a meeting always supplies it so its own name isn't outranked.

The same file is produced headlessly with `--report today json`, or by setting
`reportFormat: "json"`. Saved HTML snapshots deliberately carry **no** export: a frozen file's
embedded copy would go stale the moment the day moved on.

## Unclassified: giving activities a rule

Anything that didn't match a rule lands in the **Unclassified** tab, one row per app + window with
the time it took today (the tab shows a count, so a day needing attention says so). This is the
place to teach Tally, instead of hand-editing `rules.json`:

1. Type a **Category** (the box suggests the ones you already use — any text is fine).
2. Pick what it **applies to** — *any window of that app*, or *only this window*.
3. Click **Save rule**.

The rule is written to `rules.json` straight away, and the next refresh (a few seconds) reclassifies
the whole day: the row leaves Unclassified and shows up in the Rollup under its new category. Every
later day and report uses it too. Nothing is guessed — the app name and window title are matched
exactly as shown.

Where the rule lands in the file depends on how specific it is, because the first matching rule
wins: a **window** rule goes to the **top** (it names one thing, so it should beat anything
generic), and an **app** rule goes to the **bottom** (it covers everything that app does, so it
must not swallow rules you already had). Your comments and existing rules are left untouched.

Saved reports show the same Unclassified list read-only — a record of what still needs a rule.

## Lost time

The **Lost time** tab is the other half of that question: stretches over five minutes that ended up
on no timesheet line — idle or locked time, plus activity that matched no rule. The tab carries the
**total** rather than a count, because "2h 09m" is the thing you need to know before someone asks
where the day went. Unclassified is where you teach Tally a rule; Lost time is where you spot the
hole.

## Manual timers

Alongside the automatic tracking you can run **manual timers** — a named span you start and stop
yourself (e.g. "Ticket #123 — phone call"):

- **Global hotkeys** start/stop from anywhere, even when Tally isn't focused. Defaults are
  `Ctrl+Alt+T` (start) and `Ctrl+Alt+S` (stop). **Reconfigure them in the app** — the live
  view's **Hotkeys…** button (or the tray's **Configure hotkeys…**) opens a dialog where you
  press the combo you want; it saves to `settings.json` and rebinds immediately. You can still
  hand-edit `timerStartHotkey` / `timerStopHotkey` in `settings.json` (Ctrl/Alt/Shift/Win + a
  letter or F-key; the in-app picker captures Ctrl/Alt/Shift combos).
- **Name and start** a timer in the live view's **Timers** tab: the field and Start/Stop button sit
  under the list of what you've already recorded, so a finished timer files in above the field you
  started it from. Editing the name while it runs renames it live. However a timer was started, its
  elapsed time also shows in the top bar, so it stays visible from any tab.
- **Tray menu** has a Start/Stop timer item too.
- While a timer runs, a small **floating bubble** sits at the bottom-right showing the name +
  elapsed time, with a stop button — on top of whatever you're working in, whether the live window
  is closed, minimized, or just behind something else. It steps aside only while the live window
  itself has focus, where the timer is already on screen. **Drag it** anywhere; **double-click** it
  to reopen the app; **right-click** it to rename the timer (inline) or stop it — without opening
  the full app.

Completed timers are saved to the `manual_timers` table and listed in the report's **Timers
tab** (name, start, end, duration), and each also appears on the **Rollup** under a **Timer**
category (the detail is the timer name; timers sharing a name are summed). In the live view you
can **rename a recorded timer** by editing its name in the Timers tab — the change persists and
reflects on the Rollup — and **Delete** at the end of a row removes one you didn't mean to record.
That asks first, naming the timer and its length: recorded timers are the one thing Tally can't
rebuild from raw events, so a deleted one is gone. The running timer shows in the top bar / bubble; it joins the Timers tab
once stopped. In the JSON export a timer appears as **evidence** (`Timer: <name> (18m)`) on the
slots its time overlaps — like calls, a timer overlays your day rather than adding hours to it, so
it isn't a slot of its own.

## Reports: on demand and automatic

Reports can be generated **at any moment** — they cover the day *so far*, so a 2pm report
is a valid picture of the morning. Three triggers:

- **Tray menu** — "Generate today's / yesterday's report" (writes + opens the file).
- **CLI** — `tally.exe --report [today|yesterday|yyyy-MM-dd] [html|md]` writes the file
  headlessly (works while the tray instance runs; usable from a scheduled task). The optional
  format arg overrides the setting for that one run.
- **Automatic** — at one or more times a day. Configure them in the app under **Settings**
  (tray menu or the live view's Settings button): add each time with the picker, or remove
  them; no times = auto-reports off. They're stored as `autoReportTimes` (e.g.
  `["12:00", "17:30"]`, machine-local) in `settings.json`, and applied immediately when you
  save — no restart. Each time shows a tray balloon when ready (or set
  `openReportOnAutoGenerate: true` to pop the file open); if tally starts after a time, it
  catches up once shortly after startup. (The old single `autoReportTime` still works as a
  fallback if `autoReportTimes` isn't set.)

**Format** — reports render as **HTML by default** (a self-contained, theme-aware page that
opens in the browser: stat cards up top, then **Rollup / Timesheet / Timeline / Calls / Timers /
Unclassified / Lost time as tabs** — Rollup first — over color-coded tables). Dates display MM-dd-yyyy. Set `reportFormat` in
`settings.json` to `"markdown"` for
`.md` (stacked sections, no tabs), or `"json"` to emit the machine export directly. The page
is fully self-contained (inline CSS + a little inline JS for the tabs, no external requests),
so it's safe to keep or share as a single file. The live view uses the same tabs and keeps
your selected tab across its refreshes.

See [Timesheet export](#timesheet-export) for getting the day into att.

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

Most rules are easiest to add from the live view's **Unclassified** tab (above). To write one by
hand, edit `%USERPROFILE%\.tally\rules.json` (created with starter rules on first run; comments
allowed). Ordered, first match wins; `processPattern`/`titlePattern` are case-insensitive
regexes; named groups `(?<ticket>...)`, `(?<client>...)`, and `(?<subject>...)` extract those
fields. Rules are re-read on every report generation, so edits apply immediately.

`subject` captures a free-text "what/who" — e.g. the shipped Teams rule pulls the focused
chat/channel name out of `Chat | <name> | Microsoft Teams`, so the rollup lists each
conversation separately instead of one lumped "Teams" row.

The **Rollup** is per-activity, not per-category: each distinct browser tab, editor window,
email, or Teams chat is its own row with time summed across the whole day (so several Halo
ticket tabs open at once each track separately). Halo tickets group by ticket number; Teams
chats by name; everything else by window title, after stripping volatile title noise
("and N more pages", the trailing browser name, and an editor's leading unsaved-changes marker
`*` or `●`) so revisiting a tab — or saving a file — rolls up instead of fragmenting.

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

## Auto-update (GitHub Releases)

Installed apps update themselves from GitHub Releases (`github.com/Zac-Lutz/Tally`): the tray
app checks a few seconds after startup and every 4 hours, downloads any newer version in the
background, and applies it on the next restart (a tray note says when one's ready). No files to
send around. To grab an update immediately instead of waiting, **right-click the tray →
Check for updates** — it downloads the newest release and restarts Tally into it on the spot.

**To publish a new version, double-click `Publish-Update.cmd`** (there's also a **"Publish Tally
Update"** shortcut on the Desktop). That's the whole thing — it:

1. asks for your GitHub token **the first time only**, then stores it **encrypted** for your
   Windows user (under `%USERPROFILE%\.tally`, never in the repo) and reuses it every run after;
2. **auto-picks the next version** by bumping the patch of the latest release (1.2.0 -> 1.2.1);
3. asks you to confirm (`Publish Tally 1.2.1? [Y/n]` — Enter = yes), then builds, packs, and
   uploads the GitHub release.

So a routine update is: double-click, press Enter. Nothing to remember.

Prefer the terminal, or need a bigger version jump? The script still takes arguments:

```powershell
./Publish-Tally.ps1                  # auto-bump the patch (same as the double-click)
./Publish-Tally.ps1 -Version 1.3.0   # pin an exact version for a minor/major jump
```

The token is only ever read from your environment or the encrypted per-user file — never stored
in the repo or the app (the app fetches updates anonymously, which is why the repo must be
**public**). To change or clear the saved token, delete `%USERPROFILE%\.tally\publish-token.dat`
and you'll be asked for it again on the next publish.

**One-time bootstrap:** a version that predates this auto-update code can't check GitHub, so the
*first* time, hand each person `dist/Tally-win-Setup.exe` from a publish run (or the `Setup.exe`
attached to the GitHub release). After they're on an auto-updating build, every later version is
automatic. (Note: auto-update only runs for a **Velopack-installed** app — not the dev
`Install-Tally.ps1` build, which you rebuild locally.)

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
