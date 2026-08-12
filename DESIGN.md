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
2. Blazor UI (WinForms + BlazorWebView + Radzen): today timeline + report view.
3. Unclassified triage UI → "save as rule".
4. Persist classified blocks + manual block edits; raw-event retention/purge.
5. Polish: HKCU Run autostart, settings, real tray icon, Velopack installer.

## Known week-one risks

- **New Teams title fidelity** — Teams is WebView2; call/meeting window titles vary by call
  type. If titles are weak, the mic span still nails the *duration*; only the label suffers.
- **ScreenConnect title → client mapping** — the starter regex assumes
  `Client - ... - ScreenConnect`-shaped titles; tune `rules.json` to the real session names.
- **Mic session behavior** — if Teams holds an *active* capture session outside calls (mute
  ≠ inactive in some configs), calls will over-count; validate against a real day and fall
  back to the `CapabilityAccessManager\ConsentStore\microphone` registry approach if needed.
