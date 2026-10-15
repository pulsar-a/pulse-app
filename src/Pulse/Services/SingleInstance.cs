namespace Pulse.Services;

/// <summary>
/// Keeps Pulse to one running copy. Two copies poll the same hardware at once, which doubles the load
/// and can make controllers return errors (in testing, SATA drives went missing while a second copy ran).
/// A second launch signals the first one to come to the front, then exits.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    private const string MutexName = @"Local\Pulse.SingleInstance";
    private const string ActivateEventName = @"Local\Pulse.Activate";

    private readonly Mutex? _mutex;
    private readonly EventWaitHandle? _activate;
    private RegisteredWaitHandle? _registration;

    private SingleInstance(Mutex mutex, EventWaitHandle activate)
    {
        _mutex = mutex;
        _activate = activate;
    }

    /// <summary>
    /// Returns the guard if this is the first copy; otherwise asks the running copy to show itself
    /// and returns null.
    /// </summary>
    public static SingleInstance? TryAcquire()
    {
        try
        {
            var mutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
            if (createdNew)
                return new SingleInstance(mutex, new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName));
            mutex.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            // The running copy is elevated and this one isn't, so it can't even open the mutex.
            // Either way, another copy is running.
        }

        SignalRunningInstance();
        return null;
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a thread-pool thread) whenever a second launch happens.</summary>
    public void ListenForActivation(Action onActivate)
    {
        if (_activate is null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static void SignalRunningInstance()
    {
        try
        {
            if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
            {
                using (activate)
                    activate.Set();
            }
        }
        catch (UnauthorizedAccessException)
        {
            // A non-elevated launch can't signal an elevated copy; it just exits quietly.
        }
    }

    public void Dispose()
    {
        _registration?.Unregister(null);
        _activate?.Dispose();
        try { _mutex?.ReleaseMutex(); }
        catch (ApplicationException) { }
        _mutex?.Dispose();
    }
}
