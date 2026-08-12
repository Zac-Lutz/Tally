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
        int keys = 0, int clicks = 0, string process = "chrome")
        => new(
            new Block(T0.AddMinutes(startMinutes), T0.AddMinutes(endMinutes), process, title),
            new Classification(category, client, ticket, subject, category == Classification.Unclassified ? null : "rule"),
            keys == 0 && clicks == 0 ? BlockActivity.None : new BlockActivity(keys, clicks));

    [Fact]
    public void ProducesSelfContainedHtmlDocument()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Email", "Inbox - Outlook")], [], []);

        Assert.StartsWith("<!DOCTYPE html>", md);
        Assert.Contains("<style>", md);          // CSS is inlined — no external requests
        Assert.Contains("</html>", md);
        Assert.Contains("2026-08-12", md);
    }

    [Fact]
    public void EscapesHtmlSpecialCharactersInTitles()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
            [CB(0, 30, "Development", "diff <script> & \"quotes\"", process: "code")], [], []);

        Assert.Contains("&lt;script&gt; &amp; &quot;quotes&quot;", md);
        Assert.DoesNotContain("<script>", md);   // the raw tag must never survive into the page
    }

    [Fact]
    public void RollupSeparatesTeamsChatsAndShowsActivity()
    {
        var md = HtmlReportWriter.BuildHtml(Date,
        [
            CB(0, 15, "Teams", "Chat | Matt Longenecker | Microsoft Teams", subject: "Matt Longenecker", process: "ms-teams", keys: 40),
            CB(15, 21, "Teams", "Chat | Service Family | Microsoft Teams", subject: "Service Family", process: "ms-teams"),
        ], [], []);

        Assert.Contains("Matt Longenecker", md);
        Assert.Contains("Service Family", md);
        Assert.Contains("40/0", md);   // rollup activity cell for the 40-keystroke chat block
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

        // Scope to the Timeline section — titles also appear in the (per-tab) rollup above it.
        var timeline = md[md.IndexOf("<h2>Timeline</h2>", StringComparison.Ordinal)..];
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
    public void EmptyDaySaysSo()
    {
        var md = HtmlReportWriter.BuildHtml(Date, [], [], []);

        Assert.Contains("No activity recorded.", md);
        Assert.Contains("</html>", md);
    }
}
