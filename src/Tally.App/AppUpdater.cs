using Velopack;
using Velopack.Sources;

namespace Tally.App;

/// <summary>
/// Checks the app's GitHub Releases for a newer version and stages it to apply on the next exit.
/// Non-disruptive: it downloads in the background and never restarts mid-session. No-ops in a
/// dev/portable run (only a Velopack-installed app can self-update).
/// </summary>
internal static class AppUpdater
{
    private const string RepoUrl = "https://github.com/Zac-Lutz/Tally";

    // Call from the UI thread (e.g. a WinForms timer tick) so continuations resume there and the
    // onStaged callback can touch the tray icon safely — hence no ConfigureAwait(false).
    public static async Task CheckAndStageAsync(Action<string>? onStaged = null)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
                return;   // dev build / portable — nothing to update

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
                return;   // already current

            await manager.DownloadUpdatesAsync(update);
            manager.WaitExitThenApplyUpdates(update);   // apply when the app next exits

            var version = update.TargetFullRelease.Version.ToString();
            Log.Info($"Update {version} downloaded; will apply on next restart");
            onStaged?.Invoke(version);
        }
        catch (Exception ex)
        {
            Log.Error("Update check failed", ex);
        }
    }
}
