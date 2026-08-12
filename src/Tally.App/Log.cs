namespace Tally.App;

internal static class Log
{
    private static readonly Lock Sync = new();

    public static void Info(string message) => Write("INFO", message);

    public static void Error(string message, Exception? exception = null)
        => Write("ERROR", exception is null ? message : $"{message}: {exception}");

    private static void Write(string level, string message)
    {
        try
        {
            lock (Sync)
            {
                File.AppendAllText(
                    Path.Combine(TallyPaths.LogsDirectory, "tally.log"),
                    $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the tracker down.
        }
    }
}
