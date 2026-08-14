using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

/// <summary>User-defined categories: the file behind the Categories tab, and the colour lookup.</summary>
public class CategoriesFileTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"tally-categories-{Guid.NewGuid():N}.json");

    public void Dispose()
    {
        if (File.Exists(_path))
            File.Delete(_path);
    }

    [Fact]
    public void Load_MissingFile_IsNoCategories()
        => Assert.Empty(CategoriesFile.Load(_path));

    [Fact]
    public void Upsert_CreatesTheFile_AndRoundTrips()
    {
        CategoriesFile.Upsert(_path, "Documentation", "#8b5cf6");

        var loaded = Assert.Single(CategoriesFile.Load(_path));
        Assert.Equal("Documentation", loaded.Name);
        Assert.Equal("#8b5cf6", loaded.Color);
    }

    [Fact]
    public void Upsert_SameNameAnyCasing_RecoloursInsteadOfDuplicating()
    {
        CategoriesFile.Upsert(_path, "Documentation", "#8b5cf6");
        CategoriesFile.Upsert(_path, "documentation", "#ff0000");

        var loaded = Assert.Single(CategoriesFile.Load(_path));
        Assert.Equal("Documentation", loaded.Name);   // the original casing survives
        Assert.Equal("#ff0000", loaded.Color);
    }

    [Fact]
    public void Rename_KeepsTheColour()
    {
        CategoriesFile.Upsert(_path, "Docs", "#8b5cf6");

        Assert.True(CategoriesFile.Rename(_path, "Docs", "Documentation"));

        var loaded = Assert.Single(CategoriesFile.Load(_path));
        Assert.Equal("Documentation", loaded.Name);
        Assert.Equal("#8b5cf6", loaded.Color);
    }

    [Fact]
    public void Rename_OntoAnExistingName_Merges_AndTheTargetKeepsItsColour()
    {
        CategoriesFile.Upsert(_path, "Docs", "#8b5cf6");
        CategoriesFile.Upsert(_path, "Documentation", "#22c55e");

        Assert.True(CategoriesFile.Rename(_path, "Docs", "Documentation"));

        var loaded = Assert.Single(CategoriesFile.Load(_path));
        Assert.Equal("Documentation", loaded.Name);
        Assert.Equal("#22c55e", loaded.Color);
    }

    [Fact]
    public void Rename_UnknownName_ReturnsFalse()
        => Assert.False(CategoriesFile.Rename(_path, "Nope", "Still nope"));

    [Fact]
    public void Remove_DeletesTheEntry()
    {
        CategoriesFile.Upsert(_path, "Docs", "#8b5cf6");

        Assert.True(CategoriesFile.Remove(_path, "docs"));
        Assert.Empty(CategoriesFile.Load(_path));
        Assert.False(CategoriesFile.Remove(_path, "docs"));
    }

    // ---- CategoryPalette ----

    [Fact]
    public void Palette_MatchesNamesCaseInsensitively()
    {
        var palette = new CategoryPalette([new("Halo", "#ff0000")]);

        Assert.Equal("255,0,0", palette.CustomRgb("halo"));
        Assert.Null(palette.CustomRgb("Teams"));
    }

    [Fact]
    public void Palette_SkipsUnparseableColours_InsteadOfFailing()
    {
        var palette = new CategoryPalette([new("Bad", "reddish"), new("Good", "22c55e")]);

        Assert.Null(palette.CustomRgb("Bad"));
        Assert.Equal("34,197,94", palette.CustomRgb("Good"));   // leading # optional
    }

    [Theory]
    [InlineData("#3b82f6", "59,130,246")]
    [InlineData("3B82F6", "59,130,246")]
    public void HexToRgb_Parses(string hex, string rgb)
        => Assert.Equal(rgb, CategoryPalette.HexToRgb(hex));

    [Theory]
    [InlineData("#fff")]
    [InlineData("blue")]
    [InlineData(null)]
    public void HexToRgb_RejectsWhatIsNotASixDigitColour(string? hex)
        => Assert.Null(CategoryPalette.HexToRgb(hex));

    [Fact]
    public void RgbToHex_RoundTrips()
        => Assert.Equal("#3b82f6", CategoryPalette.RgbToHex("59,130,246"));
}
