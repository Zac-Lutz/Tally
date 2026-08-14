using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class HtmlReportWriterTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Date = new(2026, 8, 12);

    private static ClassifiedBlock CB(
        double startMinutes, double endMinutes, string category, string title,
        string? client = null, string? ticket = null, string? subject = null,
        string process = "chrome")
        => new(
            new Block(T0.AddMinutes(startMinutes), T0.AddMinutes(endMinutes), process, title),
            new Classification(category, client, ticket, subject, category == Classification.Unclassified ? null : "rule"));

    [Fact]
    public void RulesTab_RendersInTheLiveView_WithEveryRuleInOrder()
    {
        var rules = new ClassificationRule[]
        {
            new() { Id = "first", TitlePattern = "Alpha > \"x\"", Category = "A" },
            new() { Id = "second", ProcessPattern = "^b$", Category = "B", Client = "Acme" },
        };

        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "A", "Alpha")], [], [], rules: rules);

        Assert.Contains("data-panel=\"rules\"", inner);
        Assert.Contains("data-tab=\"rules\"", inner);
        Assert.Contains("Alpha &gt; &quot;x&quot;", inner);   // pattern shown, escaped
        Assert.Contains("Acme", inner);
        // Row order is file order — the numbers say which rule wins a tie.
        Assert.True(inner.IndexOf("data-id=\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("first")) + "\"", StringComparison.Ordinal)
                    < inner.IndexOf("data-id=\"" + Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("second")) + "\"", StringComparison.Ordinal));
    }

    [Fact]
    public void RulesTab_AbsentWhenNoRulesArePassed_AndFromSavedReports()
    {
        var withoutRules = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "A", "Alpha")], [], []);
        var saved = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "A", "Alpha")], [], []);

        Assert.DoesNotContain("data-panel=\"rules\"", withoutRules);
        Assert.DoesNotContain("data-panel=\"rules\"", saved);
        Assert.DoesNotContain("data-tab=\"rules\"", saved);
    }

    [Fact]
    public void CategoryDatalist_IsPresentInTheLiveView_EvenWithNothingUnclassified()
    {
        // The Rules tab's category inputs use the same suggestions the triage tab does, so the
        // datalist has to exist even on a fully classified day.
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "A", "Alpha")], [], []);

        Assert.Contains("<datalist id=\"uc-cats\">", inner);
    }

    [Fact]
    public void RollupAndTimeline_ShowTheApp_BeforeTheCategory()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 30, "Development", "Program.cs", process: "Code"),
            CB(30, 45, Classification.Unclassified, "Mystery window", process: "someapp"),
        ], [], []);

        // Both tables carry the App column, headed right before Category.
        Assert.Contains("<th>App</th><th>Category</th>", md);                                    // rollup
        Assert.Contains("<th class=\"num\">Duration</th><th>App</th><th>Category</th>", md);     // timeline
        Assert.Contains("<td>Code</td>", md);
        // Unclassified time still names its app — that's what makes it identifiable.
        Assert.Contains("<td>someapp</td>", md);
    }

    [Fact]
    public void CategoriesTab_RendersInTheLiveView_WithCustomRuleAndBuiltInNames()
    {
        var categories = new CategoryDefinition[] { new("Documentation", "#8b5cf6") };
        var rules = new ClassificationRule[] { new() { Id = "h", TitlePattern = "x", Category = "Halo" } };

        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Halo", "T")], [], [],
            rules: rules, categories: categories, palette: new CategoryPalette(categories));

        Assert.Contains("data-tab=\"categories\"", inner);
        Assert.Contains("data-panel=\"categories\"", inner);
        Assert.Contains("Documentation", inner);                       // the custom category
        Assert.Contains("1 rule", inner);                              // Halo shows its rule count
        Assert.Contains("built-in", inner);                            // Timer/Call etc. labelled
        Assert.Contains("value=\"#8b5cf6\"", inner);                   // swatch prefilled custom
        Assert.Contains("ct-add-btn", inner);                          // the add bar
    }

    [Fact]
    public void CategoriesTab_AbsentFromSavedReports()
    {
        var saved = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "A", "T")], [], []);

        Assert.DoesNotContain("data-tab=\"categories\"", saved);
        Assert.DoesNotContain("data-panel=\"categories\"", saved);
    }

    [Fact]
    public void CustomColour_WinsOverTheShippedHue_EverywhereABadgeRenders()
    {
        var palette = new CategoryPalette([new("Halo", "#ff0000")]);

        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Halo", "Ticket page")], [], [], palette: palette);

        Assert.Contains("rgba(255,0,0", md);            // the custom red
        Assert.DoesNotContain("rgba(59,130,246", md);   // Halo's shipped blue is fully replaced
    }

    [Fact]
    public void CustomCategoryNames_JoinTheDatalistSuggestions()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "A", "T")], [], [],
            categories: [new CategoryDefinition("Documentation", "#8b5cf6")]);

        Assert.Contains("<option value=\"Documentation\">", inner);
    }

    [Fact]
    public void ProducesSelfContainedHtmlDocument()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.StartsWith("<!DOCTYPE html>", md);
        Assert.Contains("<style>", md);          // CSS is inlined — no external requests
        Assert.Contains("</html>", md);
        Assert.Contains("08-12-2026", md);       // date shown MM-dd-yyyy
    }

    [Fact]
    public void EscapesHtmlSpecialCharactersInTitles()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Development", "diff <script> & \"quotes\"", process: "code")], [], []);

        Assert.Contains("&lt;script&gt; &amp; &quot;quotes&quot;", md);
        // The title's raw markup must be neutralized (the page has its own legit <script> tags).
        Assert.DoesNotContain("diff <script>", md);
    }

    [Fact]
    public void RollupSeparatesTeamsChatsBySubject()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 15, "Teams", "Chat | Matt Longenecker | Microsoft Teams", subject: "Matt Longenecker", process: "ms-teams"),
            CB(15, 21, "Teams", "Chat | Service Family | Microsoft Teams", subject: "Service Family", process: "ms-teams"),
        ], [], []);

        Assert.Contains("Matt Longenecker", md);
        Assert.Contains("Service Family", md);
    }

    [Fact]
    public void RollupPanelIncludesCalls_NotJustTheCallsTab()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Email", "Inbox - Outlook")],
            [new CallSpan(T0, T0.AddMinutes(45), "Discord", "General")],
            []);

        // Scope to the rollup panel (the call also appears in the Calls panel).
        var rollup = Panel(md, "rollup");

        Assert.Contains(">Discord<", rollup);   // a Discord call files under Discord, not "Call"
        Assert.Contains("General", rollup);     // ... and says what the call was about
    }

    [Fact]
    public void Summary_HasTotalCard_EqualToActivePlusInactive()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 120, "Development", "work")],   // 2h active
            [],
            [new InactivePeriod(T0.AddMinutes(120), T0.AddMinutes(150), InactiveReasons.Idle)]);   // 30m inactive

        Assert.Contains(">Total<", md);      // a Total card exists
        Assert.Contains("2h 30m", md);       // 2h active + 30m inactive
    }

    [Fact]
    public void Rollup_IncludesTimers_UnderTheTimerCategory()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Email", "Inbox - Outlook")], [], [],
            timers: new[] { new ManualTimer { Name = "Ticket 123 call", Start = T0, End = T0.AddMinutes(18) } });

        var rollup = md[md.IndexOf("data-panel=\"rollup\"", StringComparison.Ordinal)
            ..md.IndexOf("data-panel=\"calls\"", StringComparison.Ordinal)];

        Assert.Contains(">Timer<", rollup);           // Timer category badge in the rollup
        Assert.Contains("Ticket 123 call", rollup);   // ... with the timer name as the detail
    }

    [Fact]
    public void LiveView_TimerNames_AreEditableInputs()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Email", "Inbox")], [], [],
            timers: new[] { new ManualTimer { Id = 7, Name = "Standup", Start = T0, End = T0.AddMinutes(12) } });

        Assert.Contains("class=\"tn\"", inner);           // editable timer-name input
        Assert.Contains("data-timer-id=\"7\"", inner);    // carrying the timer id
    }

    [Fact]
    public void FileReport_TimerNames_AreReadOnly()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox")], [], [],
            timers: new[] { new ManualTimer { Id = 7, Name = "Standup", Start = T0, End = T0.AddMinutes(12) } });

        Assert.DoesNotContain("class=\"tn\"", md);   // the saved report is static
    }

    [Fact]
    public void Rollup_HidesActivitiesUnderOneMinute_ButKeepsExactlyOneMinute()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 5, "Development", "Real work"),           // 5 min - shown
            CB(10, 11, "Email", "Exactly one minute"),      // 1 min - shown (>= 1m boundary)
            CB(20, 20.5, "Browsing", "Quick glance tab"),   // 30 sec - hidden as noise
        ], [], []);

        // Scope to the rollup panel (all titles still appear in the Timeline panel). Bounded by its
        // own closing tag rather than the next panel's name, so reordering the tabs can't break it.
        var rollup = Panel(md, "rollup");

        Assert.Contains("Real work", rollup);
        Assert.Contains("Exactly one minute", rollup);
        Assert.DoesNotContain("Quick glance tab", rollup);
    }

    [Fact]
    public void LiveView_RollupTicketCells_AreEditableInputs()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Development", "Client Profiles")], [], []);

        Assert.Contains("class=\"tk\"", inner);   // an editable ticket input in the live view
        Assert.Contains("data-k=", inner);        // carrying the row's override key
    }

    [Fact]
    public void LiveView_CallRow_HasAnEditableTicketInput()
    {
        // With only a call and no window blocks, the sole rollup row is the call — and it's editable.
        var inner = HtmlReportWriter.BuildMainInner(Date, [],
            [new CallSpan(T0, T0.AddMinutes(20), "ms-teams", "Standup")], []);

        var rollup = inner[..inner.IndexOf("data-panel=\"calls\"", StringComparison.Ordinal)];
        Assert.Contains("Standup", rollup);        // the call row is in the rollup
        Assert.Contains("class=\"tk\"", rollup);   // ... with an editable ticket input
    }

    [Fact]
    public void FileReport_RollupTicketCells_AreReadOnly()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Development", "Client Profiles")], [], []);

        Assert.DoesNotContain("class=\"tk\"", md);   // the saved file report is static
    }

    [Fact]
    public void LostTimeTab_ListsLongIdleAndUnclassified_WithTheTotalOnItsTab()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 60, Classification.Unclassified, "mystery window")],
            [],
            [new InactivePeriod(T0.AddMinutes(60), T0.AddMinutes(106), InactiveReasons.Idle)]);

        // The tab badge is the total time, not a count — "how much" is the question being asked.
        Assert.Contains("data-tab=\"lost\"", md);
        Assert.Contains("<span class=\"badge\">1h 46m</span>", md);

        var panel = Panel(md, "lost");
        Assert.Contains("mystery window", panel);
        Assert.Contains("idle", panel);
    }

    [Fact]
    public void LostTimeTab_SaysSoWhenNothingIsUnaccountedFor()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 60, "Development", "Tally")], [], []);

        Assert.Contains("data-tab=\"lost\"", md);
        Assert.Contains("Nothing unaccounted for", Panel(md, "lost"));
    }

    [Fact]
    public void LostTime_IsNoLongerStuckAboveTheTabs()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 60, Classification.Unclassified, "mystery window")], [],
            [new InactivePeriod(T0.AddMinutes(60), T0.AddMinutes(106), InactiveReasons.Idle)]);

        // It used to render before the tab strip, pushing every tab down the page.
        Assert.True(md.IndexOf("class=\"tabs\"", StringComparison.Ordinal)
                    < md.IndexOf("class=\"gaps\"", StringComparison.Ordinal));
    }

    [Fact]
    public void TimelineListsNewestBlockFirst()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 30, "Development", "earlier block"),
            CB(60, 90, "Browsing", "later block"),
        ], [], []);

        // Scope to the Timeline panel — titles also appear in the (per-tab) rollup panel above it.
        var timeline = md[md.IndexOf("data-panel=\"timeline\"", StringComparison.Ordinal)..];
        Assert.True(timeline.IndexOf("later block") < timeline.IndexOf("earlier block"),
            "the later block should render above the earlier one");
    }

    [Fact]
    public void SavedSnapshot_CarriesItsOwnExport_WhenGivenOne()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], [],
            embeddedJson: "{\"schema_version\":\"2\",\"slots\":[]}");

        Assert.Contains("id=\"export-json\"", md);
        Assert.Contains("data-date=\"2026-08-12\"", md);
        Assert.Contains("id=\"tally-export\"", md);
        Assert.Contains("<dialog id=\"xr\">", md);       // the same range choice the live view asks
        Assert.Contains("{\"schema_version\":\"2\",\"slots\":[]}", md);
        Assert.Contains("data-tab=\"timesheet\"", md);
    }

    [Fact]
    public void SavedSnapshot_WithoutAnExport_HasNoButtonOrDialog()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.DoesNotContain("id=\"export-json\"", md);
        Assert.DoesNotContain("id=\"tally-export\"", md);
        Assert.DoesNotContain("<dialog", md);
        Assert.Contains("data-tab=\"timesheet\"", md);   // the preview is worth reading regardless
    }

    [Fact]
    public void SectionsAreTabbed_RollupActiveByDefault()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Email", "Inbox - Outlook")],
            [new CallSpan(T0, T0.AddMinutes(10), "ms-teams", "Standup")],
            []);

        // A tab for each section, with rollup active and the others inactive.
        Assert.Contains("data-tab=\"rollup\"", md);
        Assert.Contains("data-tab=\"calls\"", md);
        Assert.Contains("data-tab=\"timeline\"", md);
        Assert.Contains("<button class=\"tab active\" type=\"button\" data-tab=\"rollup\">", md);
        Assert.Contains("<section class=\"panel active\" data-panel=\"rollup\">", md);
        Assert.Contains("<section class=\"panel\" data-panel=\"calls\">", md);
        Assert.Contains("window.tallyApplyActiveTab", md);   // switcher present
    }

    [Fact]
    public void TimersTab_ListsManualTimers()
    {
        var timers = new[]
        {
            new ManualTimer { Name = "Ticket #123 call", Start = T0, End = T0.AddMinutes(18) },
            new ManualTimer { Name = "Standup", Start = T0.AddHours(1), End = T0.AddHours(1).AddMinutes(12) },
        };
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], [], timers: timers);

        Assert.Contains("data-tab=\"timers\"", md);
        Assert.Contains("<section class=\"panel\" data-panel=\"timers\">", md);
        Assert.Contains("Ticket #123 call", md);
        Assert.Contains("Standup", md);
        Assert.Contains("18m", md);
    }

    [Fact]
    public void TimersTab_ShowsEmptyState_WhenNoTimers()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.Contains("data-tab=\"timers\"", md);
        Assert.Contains("No timers recorded today.", md);
    }

    [Fact]
    public void CallsTab_ShowsEmptyState_WhenNoCalls()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        // The Calls tab still exists; its panel shows an empty state rather than being omitted.
        Assert.Contains("data-tab=\"calls\"", md);
        Assert.Contains("No calls recorded today.", md);
    }

    [Fact]
    public void LiveShell_HasUpdateHookAndStyles_ButNoContentYet()
    {
        var shell = HtmlReportWriter.BuildLiveShell();

        Assert.Contains("id=\"tally-live\"", shell);
        Assert.Contains("window.tallyUpdate", shell);
        Assert.Contains("<style>", shell);        // same styling as the report
        Assert.Contains("</html>", shell);
    }

    [Fact]
    public void MainInner_HasSectionsButNoPageShell_NorExportButton()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date,
            [CB(0, 30, "HaloPSA", "Ticket #1 - HaloPSA", ticket: "1")],
            [], []);

        Assert.Contains("Rollup", inner);
        Assert.Contains("Timeline", inner);
        Assert.DoesNotContain("<!DOCTYPE html>", inner);   // fragment only — no shell
        Assert.DoesNotContain("id=\"export-json\"", inner); // export lives on the file report
    }

    [Fact]
    public void MainInner_OmitsTheHeader_ButKeepsTheTabbedSections()
    {
        var blocks = new[] { CB(0, 30, "Email", "Inbox - Outlook") };
        var full = HtmlReportWriter.BuildHtml(Date, blocks, [], []);
        var inner = HtmlReportWriter.BuildMainInner(Date, blocks, [], []);

        Assert.Contains("<h1>Tally", full);              // the file report shows the Tally/date header
        Assert.DoesNotContain("<h1>Tally", inner);       // the live fragment omits it (window chrome shows it)
        Assert.Contains("data-tab=\"rollup\"", inner);   // ... but still has the same tabbed sections
        Assert.Contains("Inbox - Outlook", inner);
    }

    [Fact]
    public void EmptyDaySaysSo()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [], [], []);

        Assert.Contains("No activity recorded.", md);
        Assert.Contains("</html>", md);
    }

    [Fact]
    public void UnclassifiedTab_ListsWhatMatchedNoRule_WithACount()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 30, "Email", "Inbox - Outlook"),
            CB(30, 55, Classification.Unclassified, "Runbook.txt - Notepad", process: "notepad"),
        ], [], []);

        Assert.Contains("data-tab=\"unclassified\"", md);
        Assert.Contains("<span class=\"badge\">1</span>", md);   // the tab announces the backlog

        var panel = md[md.IndexOf("data-panel=\"unclassified\"", StringComparison.Ordinal)..];
        Assert.Contains("notepad", panel);
        Assert.Contains("Runbook.txt - Notepad", panel);
        Assert.Contains("25m", panel);
    }

    [Fact]
    public void UnclassifiedTab_SaysSoWhenEverythingMatched()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.Contains("data-tab=\"unclassified\"", md);
        Assert.DoesNotContain("<span class=\"badge\">", md);     // nothing to announce
        Assert.Contains("Nothing unclassified", md);
    }

    [Fact]
    public void UnclassifiedTab_IsSaveableInTheLiveViewOnly()
    {
        var blocks = new[] { CB(0, 25, Classification.Unclassified, "Runbook.txt - Notepad", process: "notepad") };

        var inner = HtmlReportWriter.BuildMainInner(Date, blocks, [], []);
        var file = HtmlReportWriter.BuildHtml(Date, blocks, [], []);

        // Live: a category box, a scope choice, and a save button per row, plus category suggestions.
        Assert.Contains("class=\"uc-cat\"", inner);
        Assert.Contains("class=\"uc-scope\"", inner);
        Assert.Contains("class=\"uc-save\"", inner);
        Assert.Contains("<option value=\"window\">Only this window</option>", inner);
        Assert.Contains("<datalist id=\"uc-cats\">", inner);
        // The saved file report is a record, not a working surface (the shared stylesheet still
        // carries the .uc-* rules, so look for the controls themselves).
        Assert.DoesNotContain("class=\"uc-save\"", file);
        Assert.DoesNotContain("class=\"uc-cat\"", file);
        Assert.Contains("Runbook.txt - Notepad", file);   // ... but the row is still listed
    }

    [Fact]
    public void UnclassifiedRow_CarriesTheAppAndTitleAsBase64_ForTheHostToDecode()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date,
            [CB(0, 25, Classification.Unclassified, "Runbook.txt - Notepad", process: "notepad")], [], []);

        Assert.Contains($"data-p=\"{B64("notepad")}\"", inner);
        Assert.Contains($"data-t=\"{B64("Runbook.txt - Notepad")}\"", inner);
    }

    [Fact]
    public void LiveShell_CarriesTheSaveRuleHandler()
        => Assert.Contains("type:'rule'", HtmlReportWriter.BuildLiveShell());

    [Fact]
    public void TabsRunRollupTimesheetTimelineCalls()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        var order = new[] { "rollup", "timesheet", "timeline", "calls", "timers", "unclassified" }
            .Select(t => md.IndexOf($"data-tab=\"{t}\"", StringComparison.Ordinal))
            .ToList();

        Assert.DoesNotContain(-1, order);
        Assert.Equal(order.Order(), order);
    }

    [Fact]
    public void TimersTab_PutsTheRecordedListAboveTheStartControl()
    {
        var timers = new[] { new ManualTimer { Name = "Ticket #123 call", Start = T0, End = T0.AddMinutes(18) } };
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Email", "Inbox")], [], [],
            timers: timers, timerPanel: new TimerPanelState("Next one", null));

        var panel = Panel(inner, "timers");
        Assert.True(panel.IndexOf("Ticket #123 call", StringComparison.Ordinal)
                    < panel.IndexOf("class=\"tm-name\"", StringComparison.Ordinal),
            "a finished timer should file above the field it was started from");
    }

    [Fact]
    public void TimersTab_ShowsStart_WhenNothingIsRunning()
    {
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Email", "Inbox")], [], [],
            timerPanel: new TimerPanelState("Ticket #99 call", null));

        var panel = Panel(inner, "timers");
        Assert.Contains("value=\"Ticket #99 call\"", panel);   // the pending name is kept in the field
        Assert.Contains(">Start</button>", panel);
        Assert.DoesNotContain("tm-elapsed", panel);
    }

    [Fact]
    public void TimersTab_ShowsStopAndATickingElapsed_WhileRunning()
    {
        var started = T0.AddMinutes(-3);
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Email", "Inbox")], [], [],
            timerPanel: new TimerPanelState("Ticket #99 call", started, TimeSpan.FromMinutes(3)));

        var panel = Panel(inner, "timers");
        Assert.Contains(">Stop</button>", panel);
        Assert.Contains("03:00", panel);
        // The script ticks between refreshes from this, so it has to be a parseable instant.
        Assert.Contains($"data-started=\"{started:o}\"", panel);
    }

    [Fact]
    public void TimesheetTab_AlwaysShowsTheWholeDay()
    {
        // Choosing a slice belongs to the export dialog; the tab stays the one honest picture of
        // the day, so nothing here narrows it.
        var inner = HtmlReportWriter.BuildMainInner(Date,
            [CB(0, 60, "Development", "Morning"), CB(360, 420, "Browsing", "Afternoon")], [], []);

        var panel = Panel(inner, "timesheet");
        Assert.Contains("Morning", panel);
        Assert.Contains("Afternoon", panel);
        Assert.DoesNotContain("win-from", panel);   // no range controls on the tab
    }

    [Fact]
    public void TimersTab_OffersToDeleteEachRecordedTimer()
    {
        var timers = new[]
        {
            new ManualTimer { Id = 7, Name = "Ticket #123 call", Start = T0, End = T0.AddMinutes(18) },
            new ManualTimer { Id = 9, Name = "Standup", Start = T0.AddHours(1), End = T0.AddHours(1).AddMinutes(12) },
        };
        var inner = HtmlReportWriter.BuildMainInner(Date, [CB(0, 30, "Email", "Inbox")], [], [], timers: timers);

        var panel = Panel(inner, "timers");
        // Each row carries its own id, so the button deletes the row it sits on and no other.
        Assert.Contains("class=\"tm-del\" type=\"button\" data-timer-id=\"7\"", panel);
        Assert.Contains("class=\"tm-del\" type=\"button\" data-timer-id=\"9\"", panel);
    }

    [Fact]
    public void LiveShell_CarriesTheDeleteTimerHandler()
        => Assert.Contains("type:'timerDelete'", HtmlReportWriter.BuildLiveShell());

    [Fact]
    public void SavedSnapshot_HasNoDeleteButtons()
    {
        var timers = new[] { new ManualTimer { Id = 7, Name = "Ticket #123 call", Start = T0, End = T0.AddMinutes(18) } };
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox")], [], [], timers: timers);

        var panel = Panel(md, "timers");
        Assert.Contains("Ticket #123 call", panel);
        Assert.DoesNotContain("tm-del", panel);
    }

    [Fact]
    public void SavedSnapshot_HasNoTimerControl()
    {
        var timers = new[] { new ManualTimer { Name = "Ticket #123 call", Start = T0, End = T0.AddMinutes(18) } };
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox")], [], [], timers: timers);

        var panel = Panel(md, "timers");
        Assert.Contains("Ticket #123 call", panel);      // the record is still there
        Assert.DoesNotContain("tm-name", panel);         // ... but nothing to start
        Assert.DoesNotContain("tm-go", panel);
    }

    private static string B64(string value)
        => Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(value));

    /// <summary>Just one tab's panel, so an assertion can't be satisfied by another tab's content.</summary>
    private static string Panel(string html, string name)
    {
        var start = html.IndexOf($"data-panel=\"{name}\"", StringComparison.Ordinal);
        Assert.True(start >= 0, $"no panel named '{name}'");
        var end = html.IndexOf("</section>", start, StringComparison.Ordinal);
        return end < 0 ? html[start..] : html[start..end];
    }
}
