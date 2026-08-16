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
- **A browser focus event waits half a second for the window title to settle.** Clicking back
  into a browser lands on whatever tab was last open there, and the address bar updates
  immediately while the window title lags — so reading the title at the focus event named the
  *previous* tab and stored it against the new tab's URL. It showed up once URLs gave the title
  something to be checked against: a third of browser focus events (288 of 801) disagreed with
  themselves, each one owning about a second of time filed under the wrong app, and each one a
  bad example for the phase-2 rules to be built from. The captured events put that lag within
  100ms about seven times in ten and never past 500ms, so `ForegroundWatcher` settles for
  `TitleSettleMs` before reading title and URL together — one read, one moment, no disagreement.
  Only focus changes wait; a title change already served the 1s debounce. Delivery is delayed,
  never the event: the timestamp is stamped when focus actually changed. The follow-up title
  change usually then dedupes away, so the near-duplicate pair this used to emit collapses into
  one correct row. Non-browser windows report their title accurately on arrival and still record
  synchronously.
- **Excluding is a property of a rule, scoped to an account of the day, and the builders enforce
  it.** Time at the machine that shouldn't count somewhere (a personal tab, music, a lunchtime
  video) is marked by setting `excludeFrom` on the rule that already matches it, rather than by a
  second list of anti-rules — first-match-wins keeps meaning one thing, and the row that decides
  what something *is* is the row that decides where it counts. `ExcludeScope` is
  `None | Rollup | Timesheet | Timeline | All`, because the exclusions answer different questions:
  *Rollup* tidies one view and the time still bills, *Timesheet* keeps it off the timesheet, the
  export, and the Tickets tab, and *Timeline* drops it from the blow-by-blow that is only ever
  read past. `ClassificationRule.ExcludeFrom` rides into
  `Classification.ExcludeFrom`, and `RollupBuilder`, `SuggestionSlotBuilder`, `TicketsBuilder`,
  and the Timeline each drop what their own scope excludes, **themselves**. That placement is the point: the export
  shares the Timesheet's slot builder, so filtering there makes it impossible for the file to
  disagree with the screen that reviewed it, and no future caller can forget to ask. A row still
  shown on the Timeline names what it is missing from, so an absence elsewhere reads as a decision
  rather than a bug. Lost time and Uncategorized need no filter: an excluded block carries a
  category, so it is already neither. Excluding is always a *display* decision — capture is
  untouched, so clearing a rule's exclusion brings the time back everywhere.
  The summary follows the **Timesheet** scope (`Excluded` card, subtracted
  from `Active`, still inside `Total`, omitted when zero): a Rollup-only exclusion is still on the
  timesheet, so calling it anything but Active would contradict the exported file — and with that
  rule the Timesheet tab's "actually measured" figure equals the Active card exactly.
  `"excludeFrom"` is written only when set, leaving every existing rule's shape untouched, and it
  is read through a tolerant converter: rules.json is hand-editable and a strict enum would throw,
  which — because a failed load reads as no rules at all — would cost the user every rule they
  have over one misspelled word. An exclusion saved from Uncategorized may skip the category (the
  host names it "Excluded") because deciding something is never work is a complete thought without
  also filing it.
- **The exclusion is chosen with two dropdowns, not one list.** A single list reading "Counted,
  Rollup, Timesheet, Timeline, All" never says which of those mean *exclude* — the reader has to
  already know. Picking Include or Exclude first makes the sentence read itself, and the second
  dropdown then offers only what that choice permits: one option under Include, the four scopes
  under Exclude. The page renders the pair already agreeing, so `ExcludeModeScript` only handles
  the user changing their mind, and it finds the scope select by walking forward from the mode
  select rather than by container — the same pair sits in a `div`, a `span`, and a `td`.
- **Settings is not in the tray menu.** It was the one tab the menu singled out, from back when it
  was a WinForms dialog rather than a tab. Now that the live view holds Rules, Categories, Tickets
  and the rest, a shortcut to exactly one of them only invites the question of why the others
  aren't there; the menu keeps what has no home in the live view (pause, reports, updates, folders,
  exit).
- **The Rollup is a list of categories, collapsed.** A day has a handful of categories and dozens
  of activities, so the tab opens as one line per category — biggest first, its total on the right
  — and expands on click to that category's activities, longest first. It answers "where did the
  day go" before it answers "doing what", which is the order the question actually gets asked in.
  Which categories are open lives on `window.__tallyRollupOpen` rather than in the DOM, because
  the live view replaces the whole table every ~5 seconds and a group that slammed shut mid-read
  would make the tab unusable; the click listener is delegated for the same reason. A category's
  total is summed from the rows that survived the sub-minute filter, so a header can never
  disagree with what expanding it shows. Saved reports ship the toggle script too — they are read
  offline with no app behind them.
- **A title's decorations are noise, and one of them is configured rather than guessed.**
  `TitleNormalizer` already collapsed the tab count and an editor's unsaved marker; it now also
  drops a console tool's spinner frame (`◐ ◑ ✳ …`), which had been splitting one long-running job
  into an activity per frame — fifteen minutes of one task showing as three. The browser profile
  Edge appends (`… - Work - Microsoft Edge`) is different in kind: the segment before the browser
  name is only a profile if it happens to be one, and stripping it blind would take the end off
  "Ticket 495308 - Install Teams". So profile names come from the `browserProfiles` setting and
  are matched only when a browser name follows them. They are process-wide state, set once at
  each of the three entry points (tray, `--live`, `--report`) rather than threaded through every
  caller of `Normalize` — which is also why the test suite runs serially: a test configuring a
  profile was otherwise changing what a parallel rollup test saw.
- **A line can be dropped from the export, and dropping it is permanent within the review.** Some
  of what a day honestly records — a sign-in page, a stray tray window — is real time with no
  business on a timesheet, so **Remove** (and the Delete key) takes the entry out. It is removed
  from the backing list rather than flagged, because the window's whole promise is that it shows
  exactly what the file will carry, and a struck-through row that still sits in the list quietly
  breaks that promise. The grid is rebuilt rather than patched after a removal: rows are keyed by
  position in the backing list, so every row after the removed one would otherwise point at the
  wrong entry. There is no undo — Cancel discards the whole review — and Export disables itself
  once nothing is left, with the summary saying which of the two empties it is (removed
  everything, or narrowed the range past everything).
- **An export entry says each thing once.** The title is the slot's category and the note is one
  activity per line, longest first — because the entry already carries its category, ticket and
  hours as fields, and a note reading "Development - Agent memory" spent its whole length
  restating two of them. Whatever a person can only learn from the note is what the note is for:
  what the time was actually spent on. Activity under `RollupBuilder.MinRollupDuration` is left
  out — the same threshold that keeps the Rollup a glance rather than a list — but never all of
  it: when every activity is that brief the largest still stands for the slot, since an entry
  described by nothing is worse than one described thinly. A call or timer names itself first
  (the meeting *is* the time; the windows underneath only describe it). Notes are capped by
  dropping whole lines, not by truncating, so a note too long for the importer loses its least
  important line rather than ending mid-word. Editing one field no longer rewrites another: the
  fields stopped being derived from each other the moment they stopped repeating each other.
- **A rule can match the page, and specificity gained a middle tier because of it.** A rule's
  pattern reads the address as stored, host and path with no query string. It is the sturdier way
  to recognise a web app: a tab's title changes with every click while its address holds still,
  which is exactly why the triage tab offers "any page on this site" as a third scope and drafts
  it with no app pattern at all (the same page in a second browser is the same work).
  <br>
  Placement is the subtle part. Rules sort into three tiers — a rule naming one particular thing
  on top, a site rule below those, an app-only rule at the bottom. Dropping a site rule on top
  instead would be quietly destructive, because a `^halo\.lutz\.us` rule would outrank the
  breadcrumb-title rule that reads the ticket number out of a Halo window, and the tickets would
  stop being extracted with nothing to show that anything had changed. `WithRule` therefore takes
  a `RulePlacement` and inserts a site rule after the last match rule, falling back to appending
  when there are none.
- **The window pattern and the page pattern became one.** They were two fields, ANDed, and a rule
  could carry either or both. Wanting both at once turned out to be rare — of fifty real rules,
  none did — while the split cost a column on the Rules tab and a decision on every rule written.
  So `matchPattern` is now tried against the title *and* the address, and either matching is
  enough. Old files keep working: `titlePattern` and `urlPattern` are both read as
  `matchPattern`, and a rule that carried both becomes the alternation of the two — widening it
  rather than narrowing it, so activity stays classified instead of falling back to Uncategorized.
  <br>
  The title is tried first, so its captures win when both could match: it is the more specific
  evidence, and a ticket number written into a window title names the ticket being worked where a
  path might only be a list. Measured against 7,377 real captured events, the merge moved 6 of
  them — every one a case where a stale multi-tab browser title disagreed with the address, and
  the address was right — and left the count of events carrying a ticket number unchanged at 164.
  <br>
  It does widen one thing, worth stating plainly: an address-shaped pattern is no longer confined
  to addresses, so `^halo\.lutz\.us` will now also match a File Explorer window named after the
  site. That is a fair trade rather than a bug, and there is a test pinning it so it stays a
  decision rather than a surprise.
  <br>
  The column that chose this was headed **Exclude**, which stopped being true once the control
  offered Include as well; it is now **Counting**, and the read-only cell states the outcome
  ("Not on Timesheet") rather than naming the scope, since "Timesheet" under a heading about
  counting says the opposite of what the rule does.
- **Halo ticket numbers do not come from URLs.** Halo carries the ticket in the query string
  (`/ticket?id=…`), which `UrlSanitizer` strips by design, so every Halo ticket page stores as
  bare `halo.lutz.us/ticket`. Breadcrumb-title capture already reads those numbers and remains
  the source for them; ticket-from-URL is worth having only where the path carries it (GitHub's
  `/issues/2719`, IT Glue's org and record ids).
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
- **The live view shows one chosen day, and it is the window's frame rather than a tab.** Time
  entry slips, so the day being worked on is not always today; the top bar therefore carries
  arrows, a date, and a Today button, and every tab, edit, export and snapshot below reads that one
  date. Putting the picker in the chrome rather than in a tab is the whole point — it re-frames the
  entire window, so a tab-local control would leave the other ten tabs quietly showing a different
  day. It sits at the **right**, beside Export timesheet and Generate snapshot, because those two
  act on exactly the day it names: choose the day, then do the thing to it, reading as one cluster.
  That placement costs two constraints, both about a group that is right-docked and therefore grows
  *leftwards*: the Today button stays visible and greys out rather than appearing and disappearing
  (which shifted the arrows ~90px under the cursor, precisely when stepping back several days in a
  row), and the date button is fixed-width, sized for its longest label, so the group does not
  breathe by the difference between "Today" and "Wednesday" as the day changes.
  Following today is kept as a *rule* (`_followToday`) rather than a stored date, so a window
  left open overnight still rolls over on its own, exactly as it did when today was the only
  option; re-opening the window returns to today, because the tray icon means "how is today
  going", not "resume where I was last week".
  Three consequences are deliberate: (1) a finished day switches the five-second refresh **off** and
  says **Not live · a finished day**, since nothing can arrive in it and a ticking clock beside it
  would claim otherwise; (2) the running-timer panel is offered only on today, because a timer
  started now would record against today and not the day on screen — while claiming *past* time
  (the Lost time tab, Add past timer) stays available and lands on the day shown, which is the
  main reason to open yesterday at all; (3) the back arrow stops at the first day still on record
  rather than walking into empty months, re-read from the database on each move so a retention
  purge shortens the range while the window is open.
- **The day picker is drawn, not themed.** Windows' `MonthCalendar` ignores `BackColor`,
  `TitleBackColor` and the rest on a modern desktop — it renders white, which is unreadable
  dropping out of a dark window — and `SetWindowTheme(…, "DarkMode_Explorer")` is not dependable
  across builds. So `DayPicker` lays flat buttons over `MonthGrid` (six weeks of seven, always,
  so paging a month never changes the popup's height under the cursor) in the window's own
  palette. Days outside what is still recorded are drawn but dead, which answers "how far back
  does this go" without anyone clicking to find out.
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
- **A call has two witnesses, and the window is the better one.** The microphone answers whether
  you are *talking*; a meeting answers whether you are *in a meeting*, and those differ for most
  of most meetings. Mute yourself to listen and Teams hands the microphone back, so an hour of
  meeting recorded as fifteen minutes and the rest was filed as whatever window got glanced at
  next. `CallWindowWatcher` therefore enumerates **every** visible top-level window every five
  seconds — not the foreground one, since the whole point of a meeting is that you look at other
  things during it — and `CallApps.MeetingName` decides which titles are meetings. Verified live:
  Teams gives a meeting its own top-level window while the main window keeps its own title, so
  both are visible at once and only the meeting one matches.
  <br>
  `Sessionizer` unions the two witnesses per app: either alone opens a call, overlapping spans
  fold into one, and the window's name wins because it is the meeting's own name rather than a
  guess made from whichever Teams window was focused when the mic went live. That guess is why a
  real hour-long meeting was recorded as "Chat | Service Family | Microsoft Teams".
  <br>
  The title needs normalising before it can be a key: Teams renames the window as you join and
  again when it shrinks to the compact view, and treating a rename as a new meeting would cut one
  call into three. Known section names (Chat, Calendar, Activity…) are excluded, because the main
  window titles itself the same shape and "Calendar | Microsoft Teams" is not an hour of meeting.
  Discord is deliberately not read this way — its calls don't claim time at all — and RingCentral
  waits until a real call has been observed, since a guessed window pattern is worse than none.
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
7. Look back a day (done): the live view's day picker — `DayNavigation` + `MonthGrid` (Core,
   unit-tested) behind arrows, a drawn `DayPicker` calendar, and a Today button, with every tab,
   edit, export and snapshot reading the chosen day. See the two decisions above.
   **Still open: a week or month view.** This slice deliberately stopped at one day at a time,
   because that is what filing a missed day needs and it is the loading path a range view would
   build on anyway. A week view is a different question — "where is the month going", not "what
   did I do on Thursday" — and needs its own answers for what the Timeline, the Timers tab and
   the export mean across several days before it is worth building.

## Packaging

`Package-Tally.ps1` builds a Velopack `Setup.exe` (self-contained, per-user, no admin) for
handing to another person. `VelopackApp.Build().Run()` runs first in `Main` to handle
install/update/uninstall hooks; the uninstall hook removes the autostart Run key. The app owns
autostart registration (`Autostart.cs`, gated by the `autoStart` setting), pointing the Run key
at `Environment.ProcessPath` so it works for both the dev publish and the Velopack install and
self-heals across updates. The installer is unsigned (SmartScreen "Run anyway"); add an
Authenticode cert for wider distribution.

## Known week-one risks

- ~~**New Teams title fidelity**~~ — settled the other way round: the meeting window's title is
  now the *most* reliable thing about a call, and carries the meeting's real name. It is the mic
  that turned out to be weak. Titles observed in practice are `<name> | Microsoft Teams`,
  `Meeting join | <name> | …` and `Meeting compact view | <name> | …`.
- **ScreenConnect title → client mapping** — the starter regex assumes
  `Client - ... - ScreenConnect`-shaped titles; tune `rules.json` to the real session names.
- ~~**Mic session behavior**~~ — resolved, and the risk was backwards. The worry was that Teams
  might hold an active capture session outside calls and *over*-count. What actually happens is
  the opposite: muting releases the microphone, so calls were badly *under*-counted. The window
  watcher is the fix; the registry approach was never needed.
- **RingCentral calls are still mic-only.** No RingCentral window has ever been observed in a
  capture, so there is no title pattern to key on and a guessed one would be worse than none —
  its calls end when its microphone does. Needs one real call watched before it can be fixed.
