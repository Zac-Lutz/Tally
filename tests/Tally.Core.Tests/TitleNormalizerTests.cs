using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

public class TitleNormalizerTests
{
    // Edge inserts a zero-width space (U+200B) inside "Microsoft Edge".
    private const string Edge = "Microsoft​ Edge";

    [Theory]
    [InlineData("GitHub PR #412 and 7 more pages - Work - Microsoft​ Edge", "GitHub PR #412 - Work")]
    [InlineData("GitHub PR #412 and 3 more pages - Work - Microsoft​ Edge", "GitHub PR #412 - Work")]
    [InlineData("Analytics - Work - Microsoft​ Edge", "Analytics - Work")]
    [InlineData("Some Page and 1 more tab - Google Chrome", "Some Page")]
    [InlineData("Docs - Mozilla Firefox", "Docs")]
    public void StripsBrowserChromeNoise(string input, string expected)
        => Assert.Equal(expected, TitleNormalizer.Normalize(input));

    [Fact]
    public void AConfiguredBrowserProfile_LeavesTheTitleWhenTheBrowserFollowsIt()
    {
        try
        {
            TitleNormalizer.ConfigureBrowserProfiles(["Work"]);

            Assert.Equal("Mail - Zac Franklin - Outlook",
                TitleNormalizer.Normalize($"Mail - Zac Franklin - Outlook - Work - {Edge}"));
            Assert.Equal("Orders report",
                TitleNormalizer.Normalize($"Orders report and 12 more pages - Work - {Edge}"));

            // The same word anywhere else is ordinary title text: only the segment sitting
            // immediately before the browser name is the profile.
            Assert.Equal("Work items - Sprint 4",
                TitleNormalizer.Normalize($"Work items - Sprint 4 - {Edge}"));
            // No browser, so nothing is a profile and the title is left entirely alone.
            Assert.Equal("Deploy - Work - Notepad",
                TitleNormalizer.Normalize("Deploy - Work - Notepad"));
        }
        finally
        {
            TitleNormalizer.ConfigureBrowserProfiles(null);
        }
    }

    [Fact]
    public void WithNoProfilesConfigured_TheTitleKeepsEverythingBeforeTheBrowser()
        => Assert.Equal("Analytics - Work", TitleNormalizer.Normalize($"Analytics - Work - {Edge}"));

    [Fact]
    public void ASpinningStatusGlyph_DoesNotSplitOneJobIntoSeveral()
    {
        // Straight from real capture: a console tool cycling ◐ ◑ ✳ in its title turned fourteen
        // minutes of one job into three separate activities on the export.
        var frames = new[]
        {
            "◐ Resume Tally application development",
            "◑ Resume Tally application development",
            "✳ Resume Tally application development",
            "Resume Tally application development",
        }.Select(TitleNormalizer.Normalize).Distinct().ToList();

        Assert.Equal(["Resume Tally application development"], frames);
    }

    [Fact]
    public void DiffersOnlyByTabCount_NormalizeToSameKey()
    {
        var a = TitleNormalizer.Normalize($"Orders report and 12 more pages - Work - {Edge}");
        var b = TitleNormalizer.Normalize($"Orders report and 2 more pages - Work - {Edge}");
        var c = TitleNormalizer.Normalize($"Orders report - Work - {Edge}");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
    }

    [Theory]
    [InlineData(@"*C:\Users\me\@NOTES.txt - Notepad++", @"C:\Users\me\@NOTES.txt - Notepad++")]
    [InlineData("*Untitled - Notepad", "Untitled - Notepad")]
    [InlineData("● Sessionizer.cs - tally - Visual Studio Code", "Sessionizer.cs - tally - Visual Studio Code")]
    public void StripsTheUnsavedChangesMarker(string input, string expected)
        => Assert.Equal(expected, TitleNormalizer.Normalize(input));

    [Fact]
    public void TheSameFile_DirtyOrSaved_NormalizesToOneKey()
    {
        var dirty = TitleNormalizer.Normalize(@"*C:\Users\me\@NOTES.txt - Notepad++");
        var saved = TitleNormalizer.Normalize(@"C:\Users\me\@NOTES.txt - Notepad++");

        Assert.Equal(dirty, saved);
    }

    [Fact]
    public void AnAsteriskInsideATitle_IsLeftAlone()
    {
        // Only the leading marker is chrome; a star anywhere else is part of the name.
        const string title = "Rating: 5* review - Work";
        Assert.Equal(title, TitleNormalizer.Normalize(title));
    }

    [Fact]
    public void NonBrowserTitles_AreUnchanged()
    {
        const string vs = "OrdersExportService.cs - Visual Studio";
        Assert.Equal(vs, TitleNormalizer.Normalize(vs));
    }

    [Fact]
    public void EmptyResult_FallsBackToOriginal()
    {
        // Degenerate: nothing but chrome — keep the original rather than an empty key.
        var input = "and 5 more pages";
        Assert.Equal(input, TitleNormalizer.Normalize(input));
    }
}
