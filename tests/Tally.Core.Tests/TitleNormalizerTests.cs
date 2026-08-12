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
    public void DiffersOnlyByTabCount_NormalizeToSameKey()
    {
        var a = TitleNormalizer.Normalize($"Orders report and 12 more pages - Work - {Edge}");
        var b = TitleNormalizer.Normalize($"Orders report and 2 more pages - Work - {Edge}");
        var c = TitleNormalizer.Normalize($"Orders report - Work - {Edge}");

        Assert.Equal(a, b);
        Assert.Equal(a, c);
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
