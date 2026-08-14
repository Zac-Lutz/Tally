# Tally — Design

Local-only Windows activity tracker. Captures what you actually did all day (foreground
windows, browser tabs via title changes, calls via mic usage, idle/lock time) and generates
an end-of-day markdown report to work from while entering time into HaloPSA.

Everything stays on this machine. No cloud, no telemetry.

## Decisions

- **Standalone app**, deliberately separate from att Desktop — free to iterate, no shared
  MSIX identity or CI entanglement.
- **Window titles are kept verbatim; raw events age out.** Retention is `eventRetentionDays`
  (default 90; 0 = forever; positive values floored at 7 so the last week always stays
  regenerable). `RetentionPolicy` (Core, tested) computes the cutoff at local midnight, so a
  purge removes only complete days and every retained day is fully regenerable;
  `DatabaseMaintenance` (App) deletes and VACUUMs once per local day, riding the tray's 30s
  timer (~30s after startup, then daily; a Settings save re-arms it so a shortened window
  applies on the next tick). Manual timers are never purged — user-declared and unrebuildable.
  The durable record for aged-out days is the report files already written (the default daily
  auto-report covers this); persisting classified blocks as a queryable history is still slice 5.
- **URLs come from the address bar via UI Automation, not an extension.** `BrowserUrlReader`
  reads the focused browser window's first Edit control (the address bar precedes page content in
  tree order for Chrome/Edge/Firefox) on a thread-pool thread — a WinEvent callback must return
  fast — and the event records once the URL is attached (readers order by timestamp, so delayed
  delivery is harmless). `UrlSanitizer` keeps host+path only: query strings and fragments — where
  search terms and tokens live — are stripped before storage; non-pages (half-typed searches,
  chrome:// internals) become null. Best-effort by design: any UIA failure just means no URL,
  which is the pre-capture behavior. The managed `System.Windows.Automation` client is used
  (COMReference needs Framework MSBuild; the self-contained publish ships WPF anyway). Events
  gained an additive `Url` column (ALTER TABLE, duplicate-column swallowed). Phase 2 (site-based
  rules, ticket-from-URL) and phase 3 (the export's `browser` field) build on this.
- **No input-activity counting.** An earlier version counted key-downs/clicks per block as an
  "intensity" signal (counts only, never key identity). It was removed: the signal was weak and
  redundant with idle detection (reading, calls, and reviewing are low-input but real work), and
  the low-level keyboard hook is the component most likely to trip endpoint-security/antivirus.
  Idle/lock detection alone covers "was I actually at the machine."
- **Classification `subject`** is a third capture field beside client/ticket, for a free-text
  what/who (a chat or channel name). The Teams title `Chat | <name> | Microsoft Teams` yields
  `<name>`; a channel `Chat | Team | Channel | Microsoft Teams` keeps `Team | Channel`. Discord
  follows the same shape from the other end of the title (`… - Discord`), with a process-gated
  fallback for the bare `Discord` window — chat apps title themselves by what's focused, so both
  get a specific rule plus a catch-all rather than one loose pattern.
- **Categories are named after the tool, and the browser is not a category.** The defaults file
  under the tool that earns the bill: "Halo" (was "HaloPSA"), "Outlook" (was "Email"),
  "ScreenConnect" (was "Remote Support"), plus "IT Glue". Halo's web app is matched by its
  unbranded breadcrumb titles (`^Tickets\s*>` etc., trailing number captured as the ticket) —
  measured against a real day, the old rules saw 0 of those tabs and the brand rule alone missed
  ~75% of Halo time. The browser→"Browsing" catch-all was removed: it was the single biggest
  category and said nothing billable, so unmatched tabs now land in Unclassified where the triage
  tab teaches real rules. Old category names live on as colour aliases in `HtmlReportWriter`
  because rules.json is user-owned — an installed copy keeps its old names until its user updates
  them; only fresh installs get the new defaults.
- **Rules are managed where their effects show.** The live view's Rules tab lists every rule in
  match order and edits or deletes them in place (`RulesFile.WithRuleReplacedAt` /
  `WithoutRuleAt`: text edits over the object spans the same string/comment-aware scanner finds,
  so comments and untouched rules survive byte-for-byte; a comment above a deleted rule is
  deliberately kept — comments often describe a group). An edit posts the rule's array index plus
  its id, and the host re-reads the file and checks both before writing — indexes shift whenever
  the Unclassified tab inserts a window rule at the top, so index alone could hit the wrong rule
  (delete re-verifies again after its confirmation dialog). Regexes are compiled before saving;
  a typo'd pattern is refused with a note rather than silently classifying nothing. The tab is
  live-only: BuildMainInner takes the rules list, BuildHtml never passes one — a saved report is
  a record of a day, not of the app's configuration.
- **Categories are user-definable; colours are data, not code.** `categories.json` holds
  name+hex definitions (app-owned — the Categories tab is its editor, so it round-trips through
  the serializer, unlike the comment-preserving rules.json). `CategoryPalette` resolves a
  category's RGB: user definition (case-insensitive) → shipped hue → gray, and threads through
  every badge/calendar render including saved HTML snapshots (the palette is loaded at generation
  time and bakes in). The Categories tab unions custom + rule-used + baseline + built-in names;
  recolouring ANY name stores an override, Rename also refiles rules
  (`RulesFile.WithCategoryRenamed` — exact-match, in-place per rule), Delete removes only the
  user's entry. Live-only, same gating as the Rules tab.
- **Settings live in the page, once.** The WinForms SettingsDialog was replaced by a Settings
  tab rendered like every other panel (`SettingsPanelState` re-read from settings.json per
  refresh; one `settingsSave` message posts the whole form; the host validates, writes via
  SettingsWriter, rebinds hotkeys, reschedules). The tray's Settings entry opens the live view
  on that tab (`LiveWindow.ShowTab`, with a pending-tab park for the pre-ready window) — one
  settings surface, so the two UIs can't drift. The form's `st-dirty` class joins the
  refresh-skip guard: unsaved edits survive the 5s ticks.
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
- **The export's unit is a billing target, not a run of blocks.** The first cut mapped slots to
  runs of consecutive same-category blocks; a real switch-heavy day produced **313 slots**, 110 of
  them rounding to zero hours, against att's reference sample of 3 slots over 2 days. It was
  rejected on upload and unusable if it hadn't been. `SuggestionSlotBuilder` builds what a
  timesheet books instead: a ticket if detected, else the category, within one working session.
- **Claiming lost time and backfilling are the same primitive: a recorded manual timer.** No new
  storage, no new export semantics — a timer already outranks everything, persists, lists in the
  Timers tab (rename/delete = edit/undo), and exports as an entry. The Lost time tab creates one
  over an idle/locked stretch (times trimmable first); the Timers tab's past-timer form creates
  one over any range of today (machine-off gaps have no idle record to claim). Lost time
  subtracts timer coverage with the slot builder's own interval arithmetic, so "claimed" means
  the same thing in both places and a claimed stretch leaves the tab. The host rejects a new
  timer overlapping any recorded (or the running) timer — each timer bills its whole span, so
  overlap would double-bill. Uncategorized stretches are not claimable: their fix is a rule.
- **Time is claimed in priority order so no minute is billed twice** — timer, then call, then
  window activity, each clipped around the claims above it. Meetings were the motivating case: an
  hour on a call while reading a ticket was credited to browsing/email/dev, and 2h53m of a real
  day dissolved across ten unrelated lines.
- **A live mic doesn't always mean the time is the call's.** Discord is parked in for hours while
  working, so it claims nothing and takes no slot: the focused window is the better witness, and
  Discord time that really was Discord arrives through the Discord *window*. The call is still
  attached as evidence to whatever it overlapped, so the fact isn't lost — only its claim on the
  hour is. `CallApps` holds this and the per-app naming together: two facts about one short list,
  which would drift if they lived apart. The window activity under a call/timer is retained as
  that slot's detail (it's what makes the note writable) but contributes no time. A call below the
  minimum claims nothing, so a mic blip doesn't strand time in a slot too small to keep.
- **Short work is rescued, never dropped.** Ten two-minute visits are twenty real minutes. Sessions
  under the minimum re-pool per target across the day, and the remainder combines into one visible
  "odds and ends" slot. A pooled slot's official End (and the export) stays compact — start + the
  time it earned — but the calendar draws its true story (see the next decision).
- **A ticket is an engagement; the calendar shows the envelope with the visits pinned.** Ticket
  targets get a longer session patience (`TicketSessionGap`, 30m vs 10m for categories): leaving
  the ticket window to do the work elsewhere and returning is one engagement, one entry, billing
  only the visited minutes. On the calendar, any activity slot draws over `DisplaySpan` (first
  visit → last visit, `TimesheetCalendar`) with `Visits` (blocks merged across sub-minute gaps)
  as positioned solid pins inside the faint envelope, and the hover lists each visit's times.
  Layout and grid bounds follow the envelope so stretched slots share width honestly. Calls,
  timers, and odds-and-ends keep their own span — a call is one stretch, and odds-and-ends has
  no single story. The in-between work keeps billing to its own category: this is visualization
  plus grouping, never re-attribution (a possible later step is a per-entry "bill the whole
  span" control, human-approved).
- **Reported time rounds to the nearest 5 minutes with a floor of one multiple**, never to zero:
  rounding an activity to nothing deletes work, and a zero-hour slot is rejected on import anyway.
  `Measured` and `Reported` are separate fields so the Timesheet tab can show the rounding rather
  than let the file do it quietly.
- **The export is reviewed as entries, then serialized — never edited as JSON.** `ExportEntry`
  (Core) is a slot's editable surface: title, full note text, ticket, hours; times and evidence
  stay measured fact. `JsonExportWriter.BuildEntries` → dialog edits → `BuildJson(entries…)`,
  and an unedited round-trip byte-matches the one-shot build (tested). Every contract bound is
  re-enforced at serialization, so a hand-typed note can't produce a rejected document. Edit
  semantics: a title/ticket edit recomposes the note until the note itself is hand-edited (then
  the reviewer's text wins); an edited ticket replaces the slot's work items with exactly what
  was typed; hours pass through rounded, floor 0.01. The dialog's grid shows the current range's
  entries; edits live on the entries, so changing the range never loses them.
- **Consumer bounds are enforced when writing, not hoped for.** The importer rejects the whole
  document on any field error, so `JsonExportWriter` truncates and de-duplicates to the contract's
  caps (unique ids in `[A-Za-z0-9._-]`, hours > 0, ≤10 non-empty window titles, ≤20 evidence items
  of ≤64 chars, required item titles). Bucket slugs are sanitized because categories became free
  text with the triage tab. Environment fields (machine, generated_at) arrive via
  `JsonExportContext` so Core stays deterministic/testable. `browser`/`sessions` are always empty
  (`browser` awaits the export phase of URL capture; no repo capture). `summary` is emitted only
  where it helps: omitted for a ticketed activity
  slot so the consumer default-checks the work item, always supplied for a call/timer so the
  meeting's own name isn't outranked in the note by a ticket that happened to be on screen.
- **An export window partitions by slot START, never by overlap.** Splitting a day (file the
  morning at lunch, the afternoon at close) has to cover the day exactly once; a meeting running
  through the cut-off would otherwise be billed in both halves. Start-membership makes the slices
  disjoint and exhaustive. The window lives in `SuggestionSlotOptions` and is chosen in
  `ExportRangeDialog` at export time rather than on the Timesheet tab: the tab stays the day's one
  honest picture, where a range control would leave the calendar showing a filtered day long after
  the export that needed it. The dialog recomputes its entry count and hours against the real slots
  as the range changes, so the choice is made against what it produces, not against the clock alone.
  Note the consumer replaces a whole day's suggestions on import, so a second slice clears the
  first's un-logged cards; the dialog says so once a custom range is set.
- **Both the live view and a saved snapshot can export.** The live window writes the file after
  bringing up the Timesheet tab, so what uploads is reviewed first. A snapshot embeds its own copy
  and offers the same range choice as a native `<dialog>`, filtered and downloaded in the page —
  a report from Friday can be filed on Monday with nothing but a browser. Its export is the day as
  the snapshot found it, which is what a snapshot is for; the live view is where "now" lives.
  The in-page filter reads each slot's wall clock straight out of the ISO string rather than
  through `Date`, which would re-express it in the reader's zone — "the morning" has to mean the
  morning of the machine that recorded it.
- **Mic-in-use detection** via Core Audio capture-session enumeration (NAudio), polled every
  5s. PID-based, so it joins directly onto recorded process names.
- **Day-to-day apps name their own calls.** A Teams call files as `Teams - Call`, its chats as
  `Teams - Chat`, and Discord as `Discord` whichever lane the time came from; everything else stays
  a plain `Call`. This is a short list of tools used all day, not a taxonomy — the point is that a
  timesheet can tell a meeting from a message thread without opening the Calls tab. The mapping is
  an explicit switch (`RollupBuilder.CallCategoryFor`) rather than rule-driven, because the shape
  is per-app preference — Teams wants splitting, Discord wants merging — and no general rule
  produces both. The rollup and the timesheet export share it, so a call is filed the same way
  wherever it's shown. The per-day ticket override key deliberately stays on the generic `Call`:
  it's an identity, not a label, so renaming how a call is filed can't orphan a ticket typed
  against it.
- **Calls are an overlay lane**, not foreground blocks. During a Teams call you foreground
  other windows; the call span runs independently and the report gets a separate Calls section.
  In the JSON export, calls and manual timers are `evidence` on the slots they overlap rather
  than slots of their own — they overlay the day's hours, so counting them as slots would inflate
  the total. (Cost: a timer that overlaps no block — a phone call with the screen idle — isn't
  represented. Revisit if that turns up in practice.)
- **A call ends when the mic goes quiet AND the window title changes.** Back-to-back meetings are
  the hard case: leaving one and joining the next releases the mic for ~10s, inside the 30s gap
  that stitches a momentary dropout back together, so two meetings became one span. The title
  carries the meeting name, so a gap is bridged only when the titles match. Erring toward
  splitting is deliberate — a wrong merge silently welds two meetings together, while a wrong
  split leaves two rows the rollup still sums by title.
- **A `Startup` event marks where the watchers' knowledge resumes.** `MicWatcher` holds its
  active-process set in memory, so a restart loses it: if a call ended while Tally was down, no
  `MicEnd` is ever recorded and the span would run to the end of the day, swallowing every later
  meeting. `TrayAppContext` records `Startup` as the run's first event and the sessionizer closes
  any open call span there. A call that really was still running gets a fresh `MicStart` seconds
  later and the title-matching merge rejoins it, so a mid-meeting restart still reads as one call.
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
   RegisterHotKey via a hidden message window), the Timers tab's HTML control posting
   `timerToggle`/`timerRename` to the host (the name field and Start/Stop sit under the recorded
   list, so a finished timer files in above where it was started; only the elapsed figure stays in
   the top bar, visible from any tab), and `TimerBubble`
   (borderless TopMost draggable window). `TrayAppContext` coordinates: the bubble shows only
   while a timer runs AND the live window isn't shown normally. Completed timers surface in the
   report's Timers tab, on the Rollup under a Timer category, and in the JSON export as slot
   evidence.
4. Unclassified triage UI → "save as rule" (done): an **Unclassified** tab (count badge on the tab)
   lists what matched no rule, one row per app+window (`UnclassifiedBuilder`). In the live view each
   row takes a category and a scope (any window of the app / only this window), and **Save rule**
   posts to the host, which drafts the rule (`RuleDraft`) and writes it (`RulesFile.AddRule`). Rules
   are re-read every recompute, so the next ~5s refresh reclassifies the day in place. The file
   report renders the same list read-only.
5. Raw-event retention/purge (done — see the retention decision above). Persist classified
   blocks + manual block edits: still open.
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
