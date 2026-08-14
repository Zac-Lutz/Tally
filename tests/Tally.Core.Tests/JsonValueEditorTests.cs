using System.Text.Json;
using Tally.Core;
using Xunit;

namespace Tally.Core.Tests;

public class JsonValueEditorTests
{
    private static readonly JsonDocumentOptions Options = new()
    {
        CommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    private static string ValueOf(string json, string key)
        => JsonDocument.Parse(json, Options).RootElement.GetProperty(key).GetString()!;

    [Fact]
    public void ReplacesExistingValue()
    {
        const string json = """{ "a": "x", "timerStartHotkey": "Ctrl+Alt+T", "b": 3 }""";
        var updated = JsonValueEditor.SetStringProperty(json, "timerStartHotkey", "Ctrl+Shift+P");

        Assert.Equal("Ctrl+Shift+P", ValueOf(updated, "timerStartHotkey"));
        Assert.Equal("x", ValueOf(updated, "a"));   // other props untouched
    }

    [Fact]
    public void InsertsWhenAbsent()
    {
        const string json = """{ "a": "x" }""";
        var updated = JsonValueEditor.SetStringProperty(json, "timerStopHotkey", "Ctrl+Alt+S");

        Assert.Equal("Ctrl+Alt+S", ValueOf(updated, "timerStopHotkey"));
        Assert.Equal("x", ValueOf(updated, "a"));
    }

    [Fact]
    public void PreservesComments()
    {
        var json = "{\n  // keep me\n  \"autoStart\": true,\n  \"timerStartHotkey\": \"Ctrl+Alt+T\"\n}";
        var updated = JsonValueEditor.SetStringProperty(json, "timerStartHotkey", "Alt+F9");

        Assert.Contains("// keep me", updated);
        Assert.Equal("Alt+F9", ValueOf(updated, "timerStartHotkey"));
    }

    private static string[] ArrayOf(string json, string key)
        => JsonDocument.Parse(json, Options).RootElement.GetProperty(key)
            .EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public void SetStringArray_InsertsWhenAbsent()
    {
        const string json = """{ "a": "x" }""";
        var updated = JsonValueEditor.SetStringArrayProperty(json, "autoReportTimes", ["12:00", "17:30"]);

        Assert.Equal(new[] { "12:00", "17:30" }, ArrayOf(updated, "autoReportTimes"));
        Assert.Equal("x", ValueOf(updated, "a"));
    }

    [Fact]
    public void SetStringArray_ReplacesExisting()
    {
        const string json = """{ "autoReportTimes": ["09:00"], "b": 1 }""";
        var updated = JsonValueEditor.SetStringArrayProperty(json, "autoReportTimes", ["12:00", "15:30", "17:30"]);

        Assert.Equal(new[] { "12:00", "15:30", "17:30" }, ArrayOf(updated, "autoReportTimes"));
    }

    [Fact]
    public void SetStringArray_Empty_WritesEmptyArray()
    {
        const string json = """{ "autoReportTimes": ["09:00"] }""";
        var updated = JsonValueEditor.SetStringArrayProperty(json, "autoReportTimes", []);

        Assert.Empty(ArrayOf(updated, "autoReportTimes"));
    }

    private static long NumberOf(string json, string key)
        => JsonDocument.Parse(json, Options).RootElement.GetProperty(key).GetInt64();

    [Fact]
    public void SetNumber_ReplacesExisting()
    {
        const string json = """{ "eventRetentionDays": 90, "b": "y" }""";
        var updated = JsonValueEditor.SetNumberProperty(json, "eventRetentionDays", 30);

        Assert.Equal(30, NumberOf(updated, "eventRetentionDays"));
        Assert.Equal("y", ValueOf(updated, "b"));
    }

    [Fact]
    public void SetNumber_InsertsWhenAbsent()
    {
        const string json = """{ "a": "x" }""";
        var updated = JsonValueEditor.SetNumberProperty(json, "eventRetentionDays", 90);

        Assert.Equal(90, NumberOf(updated, "eventRetentionDays"));
        Assert.Equal("x", ValueOf(updated, "a"));
    }

    [Fact]
    public void SetNumber_ReplacesExplicitNull()
    {
        const string json = """{ "eventRetentionDays": null, "b": 3 }""";
        var updated = JsonValueEditor.SetNumberProperty(json, "eventRetentionDays", 0);

        Assert.Equal(0, NumberOf(updated, "eventRetentionDays"));
        Assert.Equal(3, NumberOf(updated, "b"));
    }

    [Fact]
    public void OnlyReplacesFirstMatch_AndProducesValidJson()
    {
        const string json = """{ "timerStartHotkey": "Ctrl+Alt+T", "other": "y" }""";
        var updated = JsonValueEditor.SetStringProperty(json, "timerStartHotkey", "Win+T");

        // Parses cleanly and both values are intact.
        Assert.Equal("Win+T", ValueOf(updated, "timerStartHotkey"));
        Assert.Equal("y", ValueOf(updated, "other"));
    }
}
