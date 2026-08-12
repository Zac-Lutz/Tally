namespace Tally.Core;

/// <summary>Environment-supplied fields for a JSON export (kept out of Core so the writer stays deterministic).</summary>
public sealed record JsonExportContext(string Producer, string Machine, DateTimeOffset GeneratedAt);
