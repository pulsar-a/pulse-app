using LibreHardwareMonitor.Hardware;

namespace Pulse.Services;

/// <summary>
/// Decides which hardware to read on each polling tick. Most hardware is read every tick; drives
/// only every few seconds, since SMART reads are slow and drive temperatures change slowly.
/// When Pulse is hidden, ticks slow down. Pure logic with an injectable clock, so it's easy to test.
/// </summary>
public sealed class PollSchedule
{
    public static readonly TimeSpan StorageInterval = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan DefaultIdleInterval = TimeSpan.FromSeconds(2);

    private readonly Dictionary<string, DateTime> _lastRead = new();

    /// <summary>The user's update interval (Settings → General).</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The slowest interval to use while the window is hidden or minimized.</summary>
    public TimeSpan IdleInterval { get; set; } = DefaultIdleInterval;

    /// <summary>True while nobody is looking at Pulse (window minimized or hidden).</summary>
    public bool IsIdle { get; set; }

    /// <summary>Time between polling ticks right now.</summary>
    public TimeSpan TickInterval => IsIdle && IdleInterval > Interval ? IdleInterval : Interval;

    public TimeSpan IntervalFor(HardwareType type) =>
        type == HardwareType.Storage && StorageInterval > TickInterval ? StorageInterval : TickInterval;

    /// <summary>
    /// Whether <paramref name="hardwareId"/> should be read at <paramref name="now"/>. Ticks drift by a few
    /// milliseconds, so anything due within a tenth of a tick counts as due rather than waiting a whole tick.
    /// </summary>
    public bool IsDue(string hardwareId, HardwareType type, DateTime now)
    {
        if (!_lastRead.TryGetValue(hardwareId, out var last))
            return true;
        var slack = TickInterval / 10;
        return now - last >= IntervalFor(type) - slack;
    }

    public void MarkRead(string hardwareId, DateTime now) => _lastRead[hardwareId] = now;

    /// <summary>Forgets read times, so everything is read on the next tick (after a drive re-scan, for example).</summary>
    public void Reset() => _lastRead.Clear();
}
