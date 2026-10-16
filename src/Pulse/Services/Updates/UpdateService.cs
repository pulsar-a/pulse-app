using Velopack;
using Velopack.Sources;

namespace Pulse.Services.Updates;

/// <summary>Settings → About → Updates.</summary>
public sealed class UpdateSettings
{
    /// <summary>Check daily and install on the next exit (on by default).</summary>
    public bool AutoInstall { get; set; } = true;

    /// <summary>Uninstalling keeps settings and history unless the user turns this off (read by <see cref="UninstallCleanup"/>).</summary>
    public bool KeepDataOnUninstall { get; set; } = true;

    public DateTime? LastCheck { get; set; }
}

public enum UpdateState
{
    /// <summary>Built from source or run from a zip: updates come with the installer.</summary>
    NotInstalled,
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
    private UpdateInfo? _pending;

    /// <param name="criticalAlert">Whether a critical alert is firing right now.</param>
    public UpdateService(SettingsService settings, Func<bool> criticalAlert)
    {
        _settings = settings;
        _criticalAlert = criticalAlert;
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
            var update = await _manager.CheckForUpdatesAsync();
            _settings.Update(s => s.Updates.LastCheck = DateTime.UtcNow);
            if (update is null)
            {
                Set(UpdateState.UpToDate, $"Pulse {_manager.CurrentVersion} is the latest version.");
                return;
            }

            var version = update.TargetFullRelease.Version;
            Set(UpdateState.Downloading, $"Downloading Pulse {version}…");
            await _manager.DownloadUpdatesAsync(update, p =>
            {
                Progress = p;
                Changed?.Invoke();
            });
            _pending = update;
            Log.Write($"Update {version} downloaded");

            // Installs when Pulse exits, unless the user restarts into it sooner.
            if (_settings.Current.Updates.AutoInstall)
                _manager.WaitExitThenApplyUpdates(update.TargetFullRelease, silent: true, restart: false);
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
        if (_manager is null || _pending is null)
            return false;
        if (!UpdatePolicy.MayRestartNow(_criticalAlert()))
        {
            Set(State, "Not now: a critical alert is on. The update installs when Pulse exits.");
            return false;
        }
        Log.Write($"Restarting into {PendingVersion}");
        _manager.ApplyUpdatesAndRestart(_pending.TargetFullRelease);
        return true;
    }

    private void Set(UpdateState state, string status)
    {
        State = state;
        Status = status;
        Changed?.Invoke();
    }
}
