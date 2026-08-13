using System.Text.Json;
using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

public class JsonExportWriterTests
{
    // Build blocks at the machine-local offset so ToLocalTime is a no-op and the wall-clock in
    // the output is timezone-independent.
    private static readonly TimeSpan LocalOffset =
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 12, 8, 0, 0));

    private static readonly DateOnly Date = new(2026, 8, 12);

    private static readonly JsonExportContext Context =
        new("tally", "TEST-MACHINE", new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.FromHours(-5)));

    private static DateTimeOffset At(int h, int m) => new(2026, 8, 12, h, m, 0, LocalOffset);

    private static ClassifiedBlock CB(
        DateTimeOffset start, DateTimeOffset end, string category, string title,
        string? ticket = null, string? subject = null)
        => new(
            new Block(start, end, "proc", title),
            new Classification(category, null, ticket, subject, "rule"));

    private static JsonElement Parse(string json) => JsonDocument.Parse(json).RootElement;

    [Fact]
    public void EnvelopeMatchesSchemaVersion2()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date, [CB(At(8, 0), At(9, 0), "Development", "VS")], [], Context));

        Assert.Equal("2", root.GetProperty("schema_version").GetString());
        Assert.Equal("tally", root.GetProperty("source").GetProperty("producer").GetString());
        Assert.Equal("TEST-MACHINE", root.GetProperty("source").GetProperty("machine").GetString());
        Assert.Equal("2026-08-12T18:00:00-05:00", root.GetProperty("source").GetProperty("generated_at").GetString());
        Assert.Equal("2026-08-12", root.GetProperty("range").GetProperty("start").GetString());
        Assert.Equal("2026-08-12", root.GetProperty("range").GetProperty("end").GetString());
    }

    [Fact]
    public void ConsecutiveSameCategoryBlocks_BecomeOneSlot_WithSummedHours()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date,
        [
            CB(At(8, 0), At(8, 30), "Development", "VS"),
            CB(At(8, 30), At(9, 0), "Development", "VS"),   // same title merges in window_titles
            CB(At(9, 0), At(9, 15), "HaloPSA", "Ticket #123 - VPN"), // new category = new slot
        ], [], Context));

        var slots = root.GetProperty("slots");
        Assert.Equal(2, slots.GetArrayLength());

        var dev = slots[0];
        Assert.Equal("development", dev.GetProperty("bucket").GetString());
        Assert.Equal(1.0, dev.GetProperty("hours").GetDouble());
        Assert.StartsWith("2026-08-12T0800-development", dev.GetProperty("id").GetString());
        var windows = dev.GetProperty("window_titles");
        Assert.Equal(1, windows.GetArrayLength());                  // two blocks, one title
        Assert.Equal(3600, windows[0].GetProperty("seconds").GetInt32());
        Assert.Equal(3600, dev.GetProperty("machines")[0].GetProperty("seconds").GetInt32());
        Assert.Equal("TEST-MACHINE", dev.GetProperty("machines")[0].GetProperty("machine").GetString());

        var halo = slots[1];
        Assert.Equal("halopsa", halo.GetProperty("bucket").GetString());
        Assert.Equal(0.25, halo.GetProperty("hours").GetDouble());
    }

    [Fact]
    public void TicketBlock_ProducesWorkItem()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date,
            [CB(At(9, 0), At(9, 15), "HaloPSA", "Ticket #123 - VPN", ticket: "123")], [], Context));

        var item = root.GetProperty("slots")[0].GetProperty("items")[0];
        Assert.Equal("wi", item.GetProperty("kind").GetString());
        Assert.Equal("#123", item.GetProperty("ref").GetString());
        Assert.Equal("Ticket #123 - VPN", item.GetProperty("title").GetString());
        Assert.Equal("", item.GetProperty("description").GetString());
    }

    [Fact]
    public void ManualOverrideTicket_FlowsIntoTheExport()
    {
        var block = new ClassifiedBlock(
            new Block(At(8, 0), At(9, 0), "devenv", "Client Profiles"),
            new Classification("Development", null, null, null, "rule"),
            OverrideTicket: "777");
        var root = Parse(JsonExportWriter.BuildJson(Date, [block], [], Context));

        var item = root.GetProperty("slots")[0].GetProperty("items")[0];
        Assert.Equal("wi", item.GetProperty("kind").GetString());
        Assert.Equal("#777", item.GetProperty("ref").GetString());
    }

    [Fact]
    public void SummaryKeyIsOmitted_NotNull()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date, [CB(At(8, 0), At(9, 0), "Email", "Inbox")], [], Context));

        var slot = root.GetProperty("slots")[0];
        Assert.False(slot.TryGetProperty("summary", out _), "summary must be absent, not null");
        // Fields with no data are still present as empty arrays.
        Assert.Equal(0, slot.GetProperty("browser").GetArrayLength());
        Assert.Equal(0, slot.GetProperty("sessions").GetArrayLength());
    }

    [Fact]
    public void OverlappingCall_BecomesEvidence()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date,
            [CB(At(13, 0), At(14, 0), "Teams", "Chat | Standup | Microsoft Teams", subject: "Standup")],
            [new CallSpan(At(13, 5), At(13, 40), "ms-teams", "Sprint planning")],
            Context));

        var evidence = root.GetProperty("slots")[0].GetProperty("evidence");
        var lines = evidence.EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Teams: Standup", lines);
        Assert.Contains("Call: Sprint planning", lines);
    }

    [Fact]
    public void OverlappingManualTimer_BecomesEvidence_WithItsDuration()
    {
        var timer = new ManualTimer { Name = "Ticket #123 phone call", Start = At(13, 10), End = At(13, 28) };
        var root = Parse(JsonExportWriter.BuildJson(Date,
            [CB(At(13, 0), At(14, 0), "HaloPSA", "Ticket #123 - VPN", ticket: "123")],
            [], Context, [timer]));

        var slot = root.GetProperty("slots")[0];
        var lines = slot.GetProperty("evidence").EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.Contains("Timer: Ticket #123 phone call (18m)", lines);
        // Timers overlay the slot rather than adding to it — the hours stay the block time.
        Assert.Equal(1.0, slot.GetProperty("hours").GetDouble());
    }

    [Fact]
    public void ATimerOutsideASlot_IsNotAttachedToIt()
    {
        var timer = new ManualTimer { Name = "Evening admin", Start = At(17, 0), End = At(17, 30) };
        var root = Parse(JsonExportWriter.BuildJson(Date,
            [CB(At(8, 0), At(9, 0), "Email", "Inbox")], [], Context, [timer]));

        var lines = root.GetProperty("slots")[0].GetProperty("evidence")
            .EnumerateArray().Select(e => e.GetString()).ToList();
        Assert.DoesNotContain(lines, l => l!.StartsWith("Timer:", StringComparison.Ordinal));
    }

    [Fact]
    public void ScriptUnsafeCharactersInTitles_AreEscaped()
    {
        var json = JsonExportWriter.BuildJson(Date,
            [CB(At(8, 0), At(9, 0), "Development", "</script><b>x</b>")], [], Context);

        Assert.DoesNotContain("</script>", json);   // safe to embed in an HTML <script> block
        Assert.Contains("\\u003C", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void EmptyDay_HasNoSlots_ButValidEnvelope()
    {
        var root = Parse(JsonExportWriter.BuildJson(Date, [], [], Context));

        Assert.Equal("2", root.GetProperty("schema_version").GetString());
        Assert.Equal(0, root.GetProperty("slots").GetArrayLength());
        Assert.Equal("2026-08-12", root.GetProperty("range").GetProperty("start").GetString());
    }
}
