using Tally.Core;
using Tally.Core.Models;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>
/// What an export entry says about itself: the category names it, and the note spends every line
/// on what the time was actually spent doing — one activity per line, longest first.
/// </summary>
public class ExportNoteTests
{
    private static readonly TimeSpan LocalOffset =
        TimeZoneInfo.Local.GetUtcOffset(new DateTime(2026, 8, 15, 8, 0, 0));

    private static DateTimeOffset At(int h, int m) => new(2026, 8, 15, h, m, 0, LocalOffset);

    private static ClassifiedBlock CB(
        DateTimeOffset start, DateTimeOffset end, string category, string title, string? ticket = null)
        => new(new Block(start, end, "proc", title), new Classification(category, null, ticket, null, "rule"));

    private static string[] NoteLines(ExportEntry entry) => entry.Note.Split('\n');

    // ---- the title ----

    [Fact]
    public void TheTitle_IsTheCategory()
    {
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [CB(At(8, 0), At(9, 0), "Development", "Program.cs - tally")], []));

        Assert.Equal("Development", entry.Title);
    }

    // ---- the note ----

    [Fact]
    public void TheNote_IsOneLinePerActivity_LongestFirst()
    {
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [
                CB(At(8, 0), At(8, 10), "Development", "Middling window"),
                CB(At(8, 10), At(8, 40), "Development", "The long one"),
                CB(At(8, 40), At(8, 45), "Development", "The short one"),
            ], []));

        Assert.Equal(["The long one", "Middling window", "The short one"], NoteLines(entry));
    }

    [Fact]
    public void TheNote_SaysNothingTheEntryAlreadyCarries()
    {
        // Category, ticket and hours are each their own field; a note that repeated them left the
        // reader deciding which half to read.
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [CB(At(8, 0), At(9, 0), "Halo", "Install Teams for Mike", ticket: "495308")], []));

        Assert.Equal("Install Teams for Mike", entry.Note);
        Assert.DoesNotContain("Halo", entry.Note);
        Assert.DoesNotContain("495308", entry.Note);
    }

    [Fact]
    public void ReturningToTheSameWindow_IsStillOneLine()
    {
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [
                CB(At(8, 0), At(8, 20), "Development", "Program.cs"),
                CB(At(8, 20), At(8, 30), "Development", "README.md"),
                CB(At(8, 30), At(8, 50), "Development", "Program.cs"),
            ], []));

        // Program.cs earned 40 minutes across two visits, so it leads — and appears once.
        Assert.Equal(["Program.cs", "README.md"], NoteLines(entry));
    }

    [Fact]
    public void ActivityUnderAMinute_DoesNotEarnALine()
    {
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [
                CB(At(8, 0), At(8, 30), "Development", "The real work"),
                CB(At(8, 30), At(8, 31), "Development", "Worth a line"),
                new ClassifiedBlock(
                    new Block(At(8, 31), At(8, 31).AddSeconds(20), "proc", "A glance"),
                    new Classification("Development", null, null, null, "rule")),
            ], []));

        Assert.Equal(["The real work", "Worth a line"], NoteLines(entry));
    }

    [Fact]
    public void WhenEveryActivityIsBrief_TheBiggestStillStandsForTheSlot()
    {
        // Better a thin description than an entry that describes itself with nothing. Two
        // categories, so neither pools into a slot of its own and both land in odds and ends.
        var entry = Assert.Single(JsonExportWriter.BuildEntries(
            [
                new ClassifiedBlock(
                    new Block(At(8, 0), At(8, 0).AddSeconds(50), "proc", "Bigger glance"),
                    new Classification("Admin", null, null, null, "rule")),
                new ClassifiedBlock(
                    new Block(At(8, 2), At(8, 2).AddSeconds(20), "proc", "Smaller glance"),
                    new Classification("Development", null, null, null, "rule")),
            ], []));

        Assert.Equal("Bigger glance", entry.Note);
    }

    [Fact]
    public void ACall_NamesItselfFirst_ThenWhatWasOnScreen()
    {
        // The meeting is what the time was; the windows underneath only describe it.
        var entry = JsonExportWriter.BuildEntries(
            [CB(At(9, 0), At(9, 30), "Development", "Program.cs")],
            [new CallSpan(At(9, 0), At(9, 30), "ms-teams", "Standup")])
            .Single(e => e.Slot.Kind == SuggestionSlotKind.Call);

        Assert.Equal(["Standup", "Program.cs"], NoteLines(entry));
    }

    [Fact]
    public void ATimer_NamesItselfFirst()
    {
        var timer = new ManualTimer
        {
            Id = 1, Name = "Fix the printer", Start = At(10, 0), End = At(10, 40),
        };

        var entry = JsonExportWriter.BuildEntries([], [], [timer])
            .Single(e => e.Slot.Kind == SuggestionSlotKind.Timer);

        Assert.Equal("Fix the printer", NoteLines(entry)[0]);
    }

    [Fact]
    public void OddsAndEnds_ListsWhatItWas_RatherThanCountingIt()
    {
        // It used to say "4 short activities, none long enough to stand alone", which is a fact
        // about the slot rather than about the day.
        // Two categories, so neither re-pools into a slot big enough to stand alone.
        var entry = JsonExportWriter.BuildEntries(
            [
                CB(At(8, 0), At(8, 2), "Admin", "Inbox"),
                CB(At(9, 0), At(9, 3), "Development", "Payroll portal"),
            ], [])
            .Single(e => e.Slot.Kind == SuggestionSlotKind.OddsAndEnds);

        Assert.Equal(["Payroll portal", "Inbox"], NoteLines(entry));
        Assert.DoesNotContain("short activities", entry.Note);
    }

    // ---- the contract ----

    [Fact]
    public void ANoteTooLongForTheImporter_LosesWholeLines_NotHalfAWord()
    {
        // Each line is its own fact; truncating mid-word would leave a half-named activity.
        var blocks = Enumerable.Range(0, 40)
            .Select(i => CB(At(8, 0).AddMinutes(i * 2), At(8, 0).AddMinutes(i * 2 + 2), "Development",
                $"A window with a fairly long and descriptive name, number {i:D2}"))
            .ToArray();

        var json = JsonExportWriter.BuildJson(
            new DateOnly(2026, 8, 15), blocks, [],
            new JsonExportContext("tally", "TEST", At(18, 0)));

        var note = System.Text.Json.JsonDocument.Parse(json)
            .RootElement.GetProperty("slots")[0].GetProperty("note").GetString()!;

        Assert.True(note.Length <= 500, $"note was {note.Length} characters");
        // Every line that survived is a whole one — none was cut off mid-name.
        Assert.All(note.Split('\n'), line => Assert.Matches(@"number \d{2}$", line));
    }
}
