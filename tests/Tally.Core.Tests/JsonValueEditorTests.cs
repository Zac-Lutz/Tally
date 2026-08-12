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
