using System.Text;
using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// The Rollup tab as a list of categories: one line each with its total, biggest first, holding
/// its own activities. Everything starts collapsed, so the tab answers "where did the day go"
/// before it answers "doing what".
/// </summary>
public class RollupGroupingTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 15, 9, 0, 0, TimeSpan.FromHours(-5));
    private static readonly DateOnly Date = new(2026, 8, 15);

    private static ClassifiedBlock CB(
        double startMinutes, double endMinutes, string category, string title, string process = "chrome")
        => new(
            new Block(T0.AddMinutes(startMinutes), T0.AddMinutes(endMinutes), process, title),
            new Classification(category, null, null, null, "rule"));

    private static string Key(string category)
        => Convert.ToBase64String(Encoding.UTF8.GetBytes(category));

    // The Rollup panel alone — the Timeline and Tickets render the same activities.
    private static string Rollup(params ClassifiedBlock[] blocks)
    {
        var html = HtmlReportWriter.BuildMainInner(Date, blocks, [], []);
        var start = html.IndexOf("data-panel=\"rollup\"", StringComparison.Ordinal);
        var end = html.IndexOf("data-panel=\"timeline\"", StringComparison.Ordinal);
        return html[start..end];
    }

    [Fact]
    public void EachCategory_IsOneLineCarryingItsTotal()
    {
        var rollup = Rollup(
            CB(0, 30, "Halo", "Ticket 495308"),
            CB(30, 50, "Halo", "Ticket 460845"),
            CB(50, 65, "Outlook", "Inbox", process: "outlook"));

        // 50 minutes of Halo across two activities, on one line.
        Assert.Contains($"<tr class=\"rg\" data-cat=\"{Key("Halo")}\">", rollup);
        Assert.Contains("<td class=\"num\">50m</td>", rollup);
        Assert.Contains($"<tr class=\"rg\" data-cat=\"{Key("Outlook")}\">", rollup);
    }

    [Fact]
    public void Categories_RunLongestFirst()
    {
        var rollup = Rollup(
            CB(0, 10, "Outlook", "Inbox", process: "outlook"),
            CB(10, 70, "Halo", "Ticket 495308"),
            CB(70, 100, "Development", "Program.cs", process: "code"));

        var order = new[] { "Halo", "Development", "Outlook" }
            .Select(c => rollup.IndexOf($"data-cat=\"{Key(c)}\"", StringComparison.Ordinal))
            .ToList();

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void WithinACategory_ActivitiesRunLongestFirst()
    {
        var rollup = Rollup(
            CB(0, 5, "Halo", "Short ticket"),
            CB(5, 45, "Halo", "Long ticket"),
            CB(45, 60, "Halo", "Middling ticket"));

        var order = new[] { "Long ticket", "Middling ticket", "Short ticket" }
            .Select(t => rollup.IndexOf(t, StringComparison.Ordinal))
            .ToList();

        Assert.All(order, i => Assert.True(i >= 0));
        Assert.Equal(order.OrderBy(i => i), order);
    }

    [Fact]
    public void EveryCategory_StartsCollapsed()
    {
        var rollup = Rollup(CB(0, 30, "Halo", "Ticket 495308"), CB(30, 50, "Outlook", "Inbox", process: "outlook"));

        // The item rows are present but carry no "open" class — the CSS hides them until clicked.
        Assert.Contains("<tr class=\"rgi\"", rollup);
        Assert.DoesNotContain("class=\"rgi open\"", rollup);
        Assert.DoesNotContain("class=\"rg open\"", rollup);
    }

    [Fact]
    public void ACategoryLine_SaysHowManyActivitiesAreUnderIt()
    {
        var rollup = Rollup(
            CB(0, 30, "Halo", "Ticket 495308"),
            CB(30, 50, "Halo", "Ticket 460845"),
            CB(50, 65, "Halo", "Ticket 492806"));

        Assert.Contains("<span class=\"rg-n\">3</span>", rollup);
    }

    [Fact]
    public void ACategoryTotal_CountsExactlyWhatExpandingItShows()
    {
        // Sub-minute activity earns no row, so it must not swell the total either — otherwise the
        // header and its contents would disagree and the tab would look broken.
        var rollup = Rollup(
            CB(0, 30, "Halo", "Ticket 495308"),
            CB(30, 30.5, "Halo", "A glance at something"));

        Assert.Contains("<td class=\"num\">30m</td>", rollup);
        Assert.Contains("<span class=\"rg-n\">1</span>", rollup);
        Assert.DoesNotContain("A glance at something", rollup);
    }

    [Fact]
    public void ADayOfNothingButGlances_SaysSoRatherThanShowingAnEmptyTable()
    {
        Assert.Contains("Nothing to roll up yet", Rollup(CB(0, 0.5, "Halo", "A glance")));
    }

    [Fact]
    public void TheSavedReport_GroupsTheSameWay_AndCanStillBeExpanded()
    {
        // A saved report is read offline with no app behind it, so it ships the toggle script too.
        var html = HtmlReportWriter.BuildHtml(Date, [CB(0, 30, "Halo", "Ticket 495308")], [], []);

        Assert.Contains($"<tr class=\"rg\" data-cat=\"{Key("Halo")}\">", html);
        Assert.Contains("window.tallyApplyRollupGroups", html);
    }
}
