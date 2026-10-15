using System.Diagnostics;
using System.Windows.Threading;

namespace Pulse.Services;

/// <summary>
/// Logs whenever the UI thread is blocked long enough to feel like a freeze.
/// A background thread pings the dispatcher a few times a second and times the reply.
/// </summary>
public static class UiWatchdog
{
    private const int StallThresholdMs = 150;

    public static void Start(Dispatcher dispatcher)
    {
        var thread = new Thread(() =>
        {
            while (!dispatcher.HasShutdownStarted)
            {
                var sw = Stopwatch.StartNew();
                var ping = dispatcher.BeginInvoke(() => { }, DispatcherPriority.Input);
                ping.Wait(TimeSpan.FromSeconds(30));
                if (sw.ElapsedMilliseconds > StallThresholdMs)
                    Log.Write($"UI thread was blocked for {sw.ElapsedMilliseconds} ms");
                Thread.Sleep(250);
            }
        })
        {
            IsBackground = true,
            Name = "UI watchdog",
        };
        thread.Start();
    }
}
