# Tally — Design

Local-only Windows activity tracker. Captures what you actually did all day (foreground
windows, browser tabs via title changes, calls via mic usage, idle/lock time) and generates
an end-of-day markdown report to work from while entering time into HaloPSA.

Everything stays on this machine. No cloud, no telemetry.

## Decisions

- **Standalone app**, deliberately separate from att Desktop — free to iterate, no shared
  MSIX identity or CI entanglement.
- **Window titles are kept verbatim, indefinitely** (in derived blocks). The block history
  is the searchable time-entry record; titles are the signal. Only raw events get a
  retention setting (not yet implemented — nothing is purged in v1).
- **No input-activity counting.** An earlier version counted key-downs/clicks per block as an
  "intensity" signal (counts only, never key identity). It was removed: the signal was weak and
  redundant with idle detection (reading, calls, and reviewing are low-input but real work), and
  the low-level keyboard hook is the component most likely to trip endpoint-security/antivirus.
  Idle/lock detection alone covers "was I actually at the machine."
- **Classification `subject`** is a third capture field beside client/ticket, for a free-text
  what/who (Teams chat name today). The Teams title `Chat | <name> | Microsoft Teams` yields
  `<name>`; a channel `Chat | Team | Channel | Microsoft Teams` keeps `Team | Channel`.
- **Saved rules are placed by specificity, not appended blindly.** Rules are first-match-wins, so
  a rule written from the Unclassified tab lands where its breadth earns: a *window* rule (app +
  exact title) goes first — it names one thing and should beat the generic rules — while an *app*
  rule goes last, so it can't shadow the specific rules already in the file. Both are written as a
  text edit (`RulesFile.WithRule` scans for the `rules` array with a string/comment-aware pass), so
  the user's comments and formatting survive. Patterns are literal: only regex metacharacters are
  escaped, not whitespace, so a generated rule stays readable and hand-editable.
- **No EF migrations.** Single-writer personal app; `TallyDbContext.EnsureSchema` creates a
  fresh schema and additively `CREATE TABLE IF NOT EXISTS`es new tables on an older DB. If the
  schema grows more complex than additive tables, adopt EF migrations.
- **JSON export (`schema_version` "2").** `JsonExportWriter` maps blocks to *slots* (runs of
  consecutive same-category blocks; hours = summed active time). Environment fields (machine,
  generated_at) arrive via `JsonExportContext` so Core stays deterministic/testable. The
  in-page **Export JSON** button embeds the JSON in a `<script type="application/json">` and
  downloads it client-side via a Blob — the default STJ encoder escapes `< > &`, so the
  embedded copy can't break out of the script element. `browser`/`sessions` are always empty
  (no URL/repo capture); `summary` is modeled nullable + `WhenWritingNull` so it's omitted,
  never null. Slots can be numerous/fragmented on a switch-heavy day — acceptable and valid;
  a future coalescing heuristic could merge short cross-category interruptions.
- **Mic-in-use detection** via Core Audio capture-session enumeration (NAudio), polled every
  5s. PID-based, so it joins directly onto recorded process names.
- **Calls are an overlay lane**, not foreground blocks. During a Teams call you foreground
  other windows; the call span runs independently and the report gets a separate Calls section.
- **An active mic suppresses idle.** Sitting hands-off on a meeting is working time.
  Lock is *not* suppressed by a call — the foreground lane stops, the call lane continues.
- **UI stack (slice 2+):** WinForms shell + BlazorWebView + Radzen (Blazor Hybrid without
  MAUI). v1 is tray-menu only.

## Architecture

```
src/
  Tally.Core      net10.0          pure logic — no Win32, fully unit-tested
                                   models, Sessionizer, Classifier, RulesFile, ReportWriter
  Tally.Capture   net10.0-windows  thin interop shell
                                   ForegroundWatcher  SetWinEventHook: EVENT_SYSTEM_FOREGROUND
                                                      + EVENT_OBJECT_NAMECHANGE (tab switches),
                                                      1s title debounce
                                   IdleWatcher        GetLastInputInfo poll, IdleStart backdated
                                   SessionWatcher     SystemEvents.SessionSwitch → Lock/Unlock
                                   MicWatcher         WASAPI capture sessions → MicStart/MicEnd
  Tally.App       net10.0-windows  WinForms tray app (tally.exe)
                                   EventRecorder      Channel → SQLite, batched writes
                                   TallyDbContext     EF Core, timestamps stored as UTC ticks
                                   ReportGenerator    events → sessionize → classify → markdown
tests/
  Tally.Core.Tests                 sessionizer / classifier / report tests
```

### Event flow

1. Watchers emit `TrackedEvent` (timestamp, kind, process, verbatim title) into a channel;
   a background writer batches them into SQLite (`%USERPROFILE%\.tally\tally.db`).
2. At report time, `Sessionizer` rebuilds the day from raw events:
   - Focus/TitleChange events open/close **blocks** (a title change = a new block, which is
     how one long "Chrome" session becomes Halo ticket → OWA → ScreenConnect transitions).
   - Blocks under 5s are dropped as flickers; same-key neighbors within 10s merge.
   - Idle/lock close the foreground lane and accrue **inactive periods**.
   - MicStart/MicEnd build **call spans** per process; gaps under 30s merge; each span is
     titled from the process's window titles (in-span first, most recent before the span as
     fallback).
3. `Classifier` runs ordered first-match-wins regex rules (`%USERPROFILE%\.tally\rules.json`,
   hot-editable, comments allowed) over each block's process+title. Named groups
   `(?<ticket>)` and `(?<client>)` extract those fields. No match → Unclassified.
4. `ReportWriter` renders markdown: summary line, rollup by category/client/ticket, calls
   table, timeline with verbatim titles, and **gaps to account for** (idle stretches and
   unclassified blocks ≥ 5 min).

### Storage

Single `events` table (append-only). Blocks/calls are recomputed from events at report
time — no derived state to invalidate while the sessionizer/classifier logic is still
evolving. Persisting classified blocks (for manual edits + event purging) is a later slice.
Timestamps are `DateTimeOffset` end to end, stored as UTC ticks so range queries compare
correctly; rendered in local time.

## Slice plan

1. **(done) Thin slice** — capture → SQLite → sessionize → classify → markdown report from
   the tray menu.
2. ~~Blazor UI: today timeline + report view.~~ **Done, simpler than planned** — a WinForms
   window hosting **WebView2** that renders the same `HtmlReportWriter` output and refreshes in
   place every ~5s (`LiveWindow.cs`). Chose this over Blazor Hybrid because reusing the report
   renderer guarantees the live view and snapshots show identical data with almost no new
   rendering code. C# drives refresh: `ReportGenerator.ComputeAsync` → `BuildMainInner` →
   `ExecuteScriptAsync("tallyUpdate(...)")` swaps `<main>` innerHTML while preserving scroll (no
   reload, no flicker). WebView2 runtime is a machine dependency (present on Win11).
3. Manual timers (done): `ManualTimerService` (Core, deterministic + unit-tested) holds the
   single active timer; persistence is a callback (the EventRecorder channel, so timer writes
   serialize with event/sample writes). WinForms wires it up: `HotkeyListener` (global
   RegisterHotKey via a hidden message window), a timer bar in `LiveWindow`, and `TimerBubble`
   (borderless TopMost draggable window). `TrayAppContext` coordinates: the bubble shows only
   while a timer runs AND the live window isn't shown normally. Follow-up: surface completed
   `manual_timers` in the report/export.
4. Unclassified triage UI → "save as rule" (done): an **Unclassified** tab (count badge on the tab)
   lists what matched no rule, one row per app+window (`UnclassifiedBuilder`). In the live view each
   row takes a category and a scope (any window of the app / only this window), and **Save rule**
   posts to the host, which drafts the rule (`RuleDraft`) and writes it (`RulesFile.AddRule`). Rules
   are re-read every recompute, so the next ~5s refresh reclassifies the day in place. The file
   report renders the same list read-only.
5. Persist classified blocks + manual block edits; raw-event retention/purge.
6. Polish: HKCU Run autostart (done — self-registered), settings, real tray icon (done),
   Velopack installer (done — `Package-Tally.ps1`).

## Packaging

`Package-Tally.ps1` builds a Velopack `Setup.exe` (self-contained, per-user, no admin) for
handing to another person. `VelopackApp.Build().Run()` runs first in `Main` to handle
install/update/uninstall hooks; the uninstall hook removes the autostart Run key. The app owns
autostart registration (`Autostart.cs`, gated by the `autoStart` setting), pointing the Run key
at `Environment.ProcessPath` so it works for both the dev publish and the Velopack install and
self-heals across updates. The installer is unsigned (SmartScreen "Run anyway"); add an
Authenticode cert for wider distribution.

## Known week-one risks

- **New Teams title fidelity** — Teams is WebView2; call/meeting window titles vary by call
  type. If titles are weak, the mic span still nails the *duration*; only the label suffers.
- **ScreenConnect title → client mapping** — the starter regex assumes
  `Client - ... - ScreenConnect`-shaped titles; tune `rules.json` to the real session names.
- **Mic session behavior** — if Teams holds an *active* capture session outside calls (mute
  ≠ inactive in some configs), calls will over-count; validate against a real day and fall
  back to the `CapabilityAccessManager\ConsentStore\microphone` registry approach if needed.
