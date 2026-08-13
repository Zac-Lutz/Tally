namespace Tally.Core;

/// <summary>
/// What Tally knows about the apps calls come from — a short list of tools used all day, where
/// treating every call the same gets the day wrong. Two facts, kept together because they're about
/// the same list and would drift apart if they lived in different files.
/// </summary>
public static class CallApps
{
    /// <summary>The category a call carries when nothing more specific applies.</summary>
    public const string DefaultCategory = "Call";

    /// <summary>A Teams call — worth separating from a Teams chat when entering time.</summary>
    public const string TeamsCallCategory = "Teams - Call";

    /// <summary>Discord, whether the time went to a call or the window.</summary>
    public const string DiscordCategory = "Discord";

    /// <summary>
    /// The category a call is filed under. Teams and Discord are day-to-day tools whose time is
    /// worth naming rather than pooling into one "Call" row: a Teams call reads differently from a
    /// Teams chat on a timesheet, and Discord is one line however the time was spent there.
    /// </summary>
    public static string CategoryFor(string processName) => processName.ToLowerInvariant() switch
    {
        "ms-teams" or "msteams" or "teams" => TeamsCallCategory,
        "discord" => DiscordCategory,
        _ => DefaultCategory,
    };

    /// <summary>
    /// Whether a call in this app should outrank the window activity underneath it on a timesheet.
    /// <para>
    /// Usually yes: an hour in a meeting is an hour of meeting even though a ticket was open
    /// through it. Discord is the exception — people sit in a voice channel for hours while
    /// working, so its mic being live says nothing about what the time was for. There, the focused
    /// window is the better witness, and Discord time that really was Discord shows up anyway,
    /// because the Discord window is what you're looking at when it is.
    /// </para>
    /// </summary>
    public static bool OutranksWindowActivity(string processName)
        => !processName.Equals("discord", StringComparison.OrdinalIgnoreCase);
}
