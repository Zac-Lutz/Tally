using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class TicketOverrideTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 12, 9, 0, 0, TimeSpan.FromHours(-5));

    [Fact]
    public void Key_ForBlockAndForRow_MatchForANonTicketedActivity()
    {
        // The row's Detail is the normalized title, so the row key and the block key must agree.
        var blockKey = TicketOverrideKey.ForBlock("Development", null, null, "Client Profiles - Visual Studio");
        var rowKey = TicketOverrideKey.ForRow("Development", null, TitleNormalizer.Normalize("Client Profiles - Visual Studio"));

        Assert.Equal(blockKey, rowKey);
    }

    [Fact]
    public void Key_ForBlockAndForRow_MatchForATicketedActivity()
    {
        var blockKey = TicketOverrideKey.ForBlock("HaloPSA", "123", null, "123 - VPN drops");
        var rowKey = TicketOverrideKey.ForRow("HaloPSA", "123", "123 - VPN drops");

        Assert.Equal(blockKey, rowKey);
    }

    [Fact]
    public void OverrideTicket_ShowsOnTheRow_ButRowKeyStaysBasedOnTheOriginal()
    {
        var block = new ClassifiedBlock(
            new Block(T0, T0.AddMinutes(30), "devenv", "Client Profiles"),
            new Classification("Development", null, null, null, "rule"),
            OverrideTicket: "999");

        var row = Assert.Single(RollupBuilder.Build([block]));

        Assert.Equal("999", row.TicketRef);   // the manual ticket is what's displayed
        // The key is computed from the ORIGINAL (no ticket), so it doesn't move once one is entered.
        Assert.Equal(TicketOverrideKey.ForBlock("Development", null, null, "Client Profiles"), row.RowKey);
    }

    [Fact]
    public void OverrideTicket_DoesNotRegroupRows()
    {
        // Two different activities tagged with the same ticket stay as two rows (grouping is by the
        // original identity), each showing the override — not merged into one.
        var a = new ClassifiedBlock(new Block(T0, T0.AddMinutes(20), "devenv", "Client Profiles"),
            new Classification("Development", null, null, null, "rule"), OverrideTicket: "999");
        var b = new ClassifiedBlock(new Block(T0.AddMinutes(20), T0.AddMinutes(35), "devenv", "Roadmap"),
            new Classification("Development", null, null, null, "rule"), OverrideTicket: "999");

        var rows = RollupBuilder.Build([a, b]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.Equal("999", r.TicketRef));
    }

    [Fact]
    public void CallRows_HaveARowKey_SoTheyAreTicketEditable()
    {
        var rows = RollupBuilder.BuildCalls([new CallSpan(T0, T0.AddMinutes(15), "Discord", "General")]);

        Assert.All(rows, r => Assert.NotNull(r.RowKey));
    }

    [Fact]
    public void BuildCalls_AppliesAManualTicketOverride()
    {
        var call = new CallSpan(T0, T0.AddMinutes(15), "Discord", "General");
        var rowKey = Assert.Single(RollupBuilder.BuildCalls([call])).RowKey!;
        var overrides = new Dictionary<string, string> { [rowKey] = "321" };

        var row = Assert.Single(RollupBuilder.BuildCalls([call], overrides));

        Assert.Equal("321", row.TicketRef);   // the manual ticket shows on the call row
    }

    [Fact]
    public void CallRowKey_IgnoresHowTheCallIsFiled_SoARenameCannotOrphanATicket()
    {
        // Teams calls are filed as "Teams - Call" rather than "Call", but the override key is an
        // identity, not a label — a ticket typed against the row must survive the naming.
        var call = new CallSpan(T0, T0.AddMinutes(30), "ms-teams", "Standup");
        var row = Assert.Single(RollupBuilder.BuildCalls([call]));

        Assert.Equal(RollupBuilder.TeamsCallCategory, row.Category);
        Assert.Equal(TicketOverrideKey.ForRow(RollupBuilder.CallCategory, null, "ms-teams / Standup"), row.RowKey);
    }

    [Fact]
    public void CallRowKeys_DistinguishByApp_NotJustName()
    {
        // Two calls with the same title in different apps must not share one override.
        var a = Assert.Single(RollupBuilder.BuildCalls([new CallSpan(T0, T0.AddMinutes(10), "Discord", "General")])).RowKey;
        var b = Assert.Single(RollupBuilder.BuildCalls([new CallSpan(T0, T0.AddMinutes(10), "ms-teams", "General")])).RowKey;

        Assert.NotEqual(a, b);
    }
}
