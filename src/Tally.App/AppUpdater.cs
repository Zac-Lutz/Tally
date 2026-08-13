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

    /// <summary>
    /// On-demand update check for the tray menu. Unlike <see cref="CheckAndStageAsync"/> (which
    /// stages for the next exit), this downloads any newer release and restarts into it immediately.
    /// Reports progress via <paramref name="report"/>. Call from the UI thread so its balloons are safe.
    /// </summary>
    public static async Task CheckNowAsync(Action<string> report)
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (!manager.IsInstalled)
            {
                report("This is a dev/portable build, so it can't self-update.");
                return;
            }

            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                report($"Tally is up to date ({DisplayVersion}).");
                return;
            }

            var version = update.TargetFullRelease.Version.ToString();
            report($"Downloading update v{version}…");
            await manager.DownloadUpdatesAsync(update);
            report($"Update v{version} ready — restarting Tally to apply…");
            manager.ApplyUpdatesAndRestart(update.TargetFullRelease, []);
        }
        catch (Exception ex)
        {
            Log.Error("Manual update check failed", ex);
            report("Update check failed — see the logs.");
        }
    }

    /// <summary>
    /// The running release version for display (e.g. "v1.2.2") when this is a Velopack-installed
    /// build, or "dev" for a from-source / portable run that isn't a real release. Computed once;
    /// reads local Velopack metadata only, so it's safe offline and off the network.
    /// </summary>
    public static string DisplayVersion { get; } = ResolveDisplayVersion();

    private static string ResolveDisplayVersion()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: false));
            if (manager.IsInstalled && manager.CurrentVersion is { } v)
                return $"v{v}";
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read the installed version", ex);
        }

        return "dev";
    }
}
