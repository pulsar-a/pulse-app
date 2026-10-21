using Velopack;
using Velopack.Sources;

namespace Pulse.Services.Updates;

/// <summary>Which releases count as updates.</summary>
public enum UpdateChannel
{
    /// <summary>Stable releases only.</summary>
    Stable,

    /// <summary>Pre-releases (betas) too.</summary>
    Beta,
}

/// <summary>Settings → About → Updates.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check daily and install on the next exit (on by default).</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>The user's choice of releases; null until they make one, which means stable.</summary>
    public UpdateChannel? Channel { get; set; }

    /// <summary>Uninstalling keeps settings and history unless the user turns this off (read by <see cref="UninstallCleanup"/>).</summary>
    public bool KeepDataOnUninstall { get; set; } = true;

    public DateTime? LastCheck { get; set; }
}

public enum UpdateState
{
    /// <summary>Built from source or run from a zip: updates come with the installer.</summary>
    NotInstalled,

    /// <summary>Installed from the Microsoft Store, which updates it.</summary>
    Store,
    Idle,
    Checking,
    UpToDate,
    Downloading,

    /// <summary>Downloaded: installs when Pulse exits, or now with a restart.</summary>
    Ready,
    Failed,
}

/// <summary>When to check and when an update may restart Pulse. Pure, so it's tested on its own.</summary>
public static class UpdatePolicy
{
    public static readonly TimeSpan CheckEvery = TimeSpan.FromDays(1);

    /// <summary>The first check waits a little after start, so it never competes with the first readings.</summary>
    public static readonly TimeSpan FirstCheckDelay = TimeSpan.FromMinutes(2);

    public static bool ShouldCheck(bool autoInstall, DateTime? lastCheck, DateTime now) =>
        autoInstall && (lastCheck is not { } last || now - last >= CheckEvery || last > now);

    /// <summary>
    /// The channel in effect: stable unless the user turned betas on, even on a beta (betas are strictly opt-in).
    /// Stable on a beta never downgrades: Velopack only offers versions newer than the running one, so a beta
    /// stays put until a newer stable release is out.
    /// </summary>
    public static UpdateChannel Channel(UpdateChannel? chosen) => chosen ?? UpdateChannel.Stable;

    /// <summary>A restart takes the monitoring away for a moment: never while a critical alert (a stopped pump) is on.</summary>
    public static bool MayRestartNow(bool criticalAlert) => !criticalAlert;
}

/// <summary>
/// Updates through Velopack from GitHub Releases. A daily check (when automatic updates are on) downloads a new
/// version in the background and arranges for it to install when Pulse exits, so the next start, often the
/// next sign-in, is the new version. Settings → About can also check now and restart straight into it.
/// UI thread for state; the work itself is async.
/// </summary>
public sealed class UpdateService
{
    public const string RepositoryUrl = "https://github.com/pulsar-a/pulse-app";

    private readonly SettingsService _settings;
    private readonly Func<bool> _criticalAlert;
    private readonly UpdateManager? _manager;
    private UpdateManager? _active;
    private UpdateInfo? _pending;

    /// <param name="criticalAlert">Whether a critical alert is firing right now.</param>
    public UpdateService(SettingsService settings, Func<bool> criticalAlert)
    {
        _settings = settings;
        _criticalAlert = criticalAlert;
        if (AppPackage.IsPackaged)
        {
            State = UpdateState.Store;
            return;
        }
        try
        {
            var manager = new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: false));
            _manager = manager.IsInstalled ? manager : null;
        }
        catch (Exception ex)
        {
            Log.Write($"Updates unavailable: {ex.Message}");
        }
        State = _manager is null ? UpdateState.NotInstalled : UpdateState.Idle;
    }

    public UpdateState State { get; private set; }

    /// <summary>A line for Settings.</summary>
    public string Status { get; private set; } = "";

    /// <summary>The version downloaded and waiting, if any.</summary>
    public string? PendingVersion => _pending?.TargetFullRelease.Version.ToString();

    public int Progress { get; private set; }

    /// <summary>The channel in effect right now (see <see cref="UpdatePolicy.Channel"/>).</summary>
    public UpdateChannel Channel => UpdatePolicy.Channel(_settings.Current.Updates.Channel);

    public event Action? Changed;

    /// <summary>The automatic check, called on a timer: does nothing unless it's due.</summary>
    public Task CheckIfDueAsync() =>
        _manager is not null && UpdatePolicy.ShouldCheck(_settings.Current.Updates.AutoInstall, _settings.Current.Updates.LastCheck, DateTime.UtcNow)
            ? CheckAsync()
            : Task.CompletedTask;

    /// <summary>Checks GitHub, and downloads a newer version if there is one.</summary>
    public async Task CheckAsync()
    {
        if (_manager is null || State is UpdateState.Checking or UpdateState.Downloading)
            return;
        Set(UpdateState.Checking, "Checking for updates…");
        try
        {
            // The same app either way; only which releases on GitHub count.
            var betas = Channel == UpdateChannel.Beta;
            var manager = betas ? new UpdateManager(new GithubSource(RepositoryUrl, accessToken: null, prerelease: true)) : _manager;
            var update = await manager.CheckForUpdatesAsync();
            _settings.Update(s => s.Updates.LastCheck = DateTime.UtcNow);
            if (update is null)
            {
                Set(UpdateState.UpToDate, betas
                    ? $"Pulse {_manager.CurrentVersion} is the latest version, betas included."
                    : $"Pulse {_manager.CurrentVersion} is the latest stable version.");
                return;
            }

            var version = update.TargetFullRelease.Version;
            Set(UpdateState.Downloading, $"Downloading Pulse {version}…");
            await manager.DownloadUpdatesAsync(update, p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            _pending = update;
            _active = manager;
            Log.Write($"Update {version} downloaded");

            // Installs when Pulse exits, unless the user restarts into it sooner.
            if (_settings.Current.Updates.AutoInstall)
                manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
            Set(UpdateState.Ready, $"Pulse {version} is ready. It installs when Pulse exits, or restart now.");
        }
        catch (Exception ex)
        {
            Log.Write($"Update check failed: {ex.Message}");
            Set(UpdateState.Failed, "Couldn't check for updates. Pulse tries again tomorrow.");
        }
    }

    /// <summary>Restarts into the downloaded version, unless a critical alert is on.</summary>
    public bool RestartToUpdate()
    {
        if (_active is null || _pending is null)
            return false;
        if (!UpdatePolicy.MayRestartNow(_criticalAlert()))
        {
            Set(State, "Not now: a critical alert is on. The update installs when Pulse exits.");
            return false;
        }
        Log.Write($"Restarting into {PendingVersion}");
        _active.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        return true;
    }

    private void Set(UpdateState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
