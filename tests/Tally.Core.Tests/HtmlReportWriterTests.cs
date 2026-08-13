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

        // Scope to the rollup panel (the call also appears in the Calls panel that follows it).
        var start = md.IndexOf("data-panel=\"rollup\"", StringComparison.Ordinal);
        var end = md.IndexOf("data-panel=\"calls\"", StringComparison.Ordinal);
        var rollup = md[start..end];

        Assert.Contains(">Call<", rollup);      // the Call category badge is in the rollup
        Assert.Contains("Discord", rollup);     // the app
        Assert.Contains("General", rollup);     // ... and what the call was about
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

        // Scope to the rollup panel (all titles still appear in the Timeline panel below it).
        var rollup = md[md.IndexOf("data-panel=\"rollup\"", StringComparison.Ordinal)
            ..md.IndexOf("data-panel=\"calls\"", StringComparison.Ordinal)];

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
    public void GapsSectionAppearsForLongIdleAndUnclassified()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 60, Classification.Unclassified, "mystery window")],
            [],
            [new InactivePeriod(T0.AddMinutes(60), T0.AddMinutes(106), InactiveReasons.Idle)]);

        Assert.Contains("Gaps to account for", md);
        Assert.Contains("mystery window", md);
        Assert.Contains("idle", md);
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
    public void ExportButtonAndEmbeddedJson_AppearWhenJsonProvided()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Email", "Inbox - Outlook")], [], [],
            embeddedJson: "{\"schema_version\":\"2\"}");

        Assert.Contains("id=\"export-json\"", md);
        Assert.Contains("data-filename=\"tally-2026-08-12.json\"", md);
        Assert.Contains("id=\"tally-export\"", md);
        Assert.Contains("{\"schema_version\":\"2\"}", md);
    }

    [Fact]
    public void NoExportButton_WhenJsonNotProvided()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.DoesNotContain("id=\"export-json\"", md);
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
}
