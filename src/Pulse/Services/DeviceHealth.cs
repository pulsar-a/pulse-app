namespace Pulse.Services;

/// <summary>What to do about a device whose read just failed.</summary>
public enum FailureAction
{
    /// <summary>Skip it for a little while and try again.</summary>
    Wait,

    /// <summary>It keeps failing: close and reopen it (a USB device that was reset needs a fresh connection).</summary>
    Reopen,
}

/// <summary>
/// Keeps one misbehaving device from disturbing the rest. A Corsair PSU on USB can vanish for a moment
/// (iCUE resets it, USB re-enumerates) and its next read throws; before, that stopped polling for
/// every sensor. Now the device is skipped with a growing pause (1, 2, 4... up to 30 seconds) while
/// everything else carries on, and after a few failures in a row it's reopened, at most every 30 seconds.
/// Pure logic with the time passed in, so it's easy to test.
/// </summary>
public sealed class DeviceHealth
{
    public const int FailuresBeforeReopen = 3;
    public static readonly TimeSpan MaxPause = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ReopenSpacing = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan LogSpacing = TimeSpan.FromMinutes(5);

    private readonly Dictionary<string, State> _devices = new();

    private sealed class State
    {
        public int Failures;
        public DateTime SkipUntil;
        public DateTime LastReopen = DateTime.MinValue;
        public DateTime LastLogged = DateTime.MinValue;
        public int Unlogged;
    }

    /// <summary>False while a failed device is in its pause.</summary>
    public bool CanRead(string id, DateTime now) => !_devices.TryGetValue(id, out var s) || now >= s.SkipUntil;

    public void Succeeded(string id, string name)
    {
        if (_devices.Remove(id, out var s) && s.Failures > 0)
            Log.Write($"{name} is reading again after {s.Failures} failed attempt(s)");
    }

    public FailureAction Failed(string id, string name, Exception error, DateTime now)
    {
        if (!_devices.TryGetValue(id, out var s))
            _devices[id] = s = new State();

        s.Failures++;
        var pause = TimeSpan.FromSeconds(Math.Min(Math.Pow(2, s.Failures - 1), MaxPause.TotalSeconds));
        s.SkipUntil = now + pause;

        // The first failure is logged in full; after that a count every few minutes, not a line per second.
        if (now - s.LastLogged >= LogSpacing)
        {
            var more = s.Unlogged > 0 ? $" ({s.Unlogged} more since the last note)" : "";
            Log.Write($"Couldn't read {name}: {error.Message}{more}. Other sensors carry on; retrying in {pause.TotalSeconds:0} s.");
            s.LastLogged = now;
            s.Unlogged = 0;
        }
        else
        {
            s.Unlogged++;
        }

        if (s.Failures >= FailuresBeforeReopen && now - s.LastReopen >= ReopenSpacing)
        {
            s.LastReopen = now;
            return FailureAction.Reopen;
        }
        return FailureAction.Wait;
    }
}
