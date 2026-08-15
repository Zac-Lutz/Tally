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

    /// <summary>RingCentral — the phone system, so a call there is simply the work.</summary>
    public const string RingCentralCategory = "RingCentral";

    /// <summary>
    /// The category a call is filed under. These are day-to-day tools whose time is worth naming
    /// rather than pooling into one "Call" row: a Teams call reads differently from a Teams chat on
    /// a timesheet, while Discord and RingCentral each want one line however the time was spent.
    /// </summary>
    public static string CategoryFor(string processName)
    {
        var app = processName.ToLowerInvariant();
        return app switch
        {
            "ms-teams" or "msteams" or "teams" => TeamsCallCategory,
            "discord" => DiscordCategory,
            // Matched on the prefix: RingCentral ships under several executable names over the
            // years (the app, the older phone client, meetings) and they're all RingCentral.
            _ when app.StartsWith("ringcentral", StringComparison.Ordinal) => RingCentralCategory,
            _ => DefaultCategory,
        };
    }

    /// Sections of the Teams app that title the main window the same shape a meeting does. Without
    /// these, opening the calendar would read as an hour-long meeting called "Calendar".
    private static readonly string[] TeamsSections =
        ["Activity", "Chat", "Teams", "Calendar", "Calls", "Files", "Apps", "Help", "Store", "Settings"];

    // Teams renames the meeting window as the meeting proceeds — joining, then the meeting itself,
    // then the small floating window when you look at something else. All three are the same
    // meeting, so the prefixes come off and the name underneath is what identifies it.
    private static readonly string[] TeamsMeetingPrefixes =
        ["Meeting join | ", "Meeting compact view | ", "Meeting | "];

    private const string TeamsSuffix = " | Microsoft Teams";

    /// <summary>
    /// The meeting a window's title names, or null when the window isn't a call window at all.
    /// <para>
    /// This is what lets Tally know a call is running without asking the microphone. The mic was
    /// the only signal once, and it answers the wrong question: it says whether you are
    /// <em>talking</em>, not whether you are <em>in a meeting</em>. Mute yourself and Teams hands
    /// the microphone back, so an hour of listening recorded as nothing at all.
    /// </para>
    /// <para>
    /// Teams is the only app read this way for now. Discord is deliberately excluded — see
    /// <see cref="OutranksWindowActivity"/> for why its calls don't get to claim time — and
    /// RingCentral needs a real call observed before its windows can be named with any confidence.
    /// </para>
    /// </summary>
    public static string? MeetingName(string processName, string title)
    {
        if (CategoryFor(processName) != TeamsCallCategory)
            return null;

        var trimmed = title.Trim();

        // The bare shell window ("Microsoft Teams") has no suffix to strip and names no meeting.
        if (!trimmed.EndsWith(TeamsSuffix, StringComparison.OrdinalIgnoreCase))
            return null;

        var name = trimmed[..^TeamsSuffix.Length].Trim();
        if (name.Length == 0)
            return null;

        foreach (var prefix in TeamsMeetingPrefixes)
        {
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                name = name[prefix.Length..].Trim();
                break;
            }
        }

        if (name.Length == 0)
            return null;

        // "Chat | Service Family" is the conversation about the meeting, not the meeting. Whatever
        // is left still carrying a section name in front of it is the main window, not a call.
        foreach (var section in TeamsSections)
        {
            if (name.StartsWith(section + " |", StringComparison.OrdinalIgnoreCase)
                || string.Equals(name, section, StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }
        }

        return name;
    }

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
