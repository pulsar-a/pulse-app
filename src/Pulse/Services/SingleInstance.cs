using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Pulse.Services;

/// <summary>
/// Keeps Pulse to one running copy. Two copies poll the same hardware at once, which doubles the load
/// and can make controllers return errors (in testing, SATA drives went missing while a second copy ran).
/// A second launch signals the first one to come to the front, then exits.
///
/// The running copy is usually elevated and a new launch usually isn't (Pulse elevates itself, see
/// <see cref="Elevation"/>), so the activation event is created with a security descriptor that lets the same
/// user signal it from a non-elevated process. By default Windows blocks writes "up" an integrity level.
/// </summary>
public sealed class SingleInstance : IDisposable
{
    /// <summary>The name the mutex and event are made from. Tests use their own, so they never touch a running Pulse.</summary>
    internal static string Name { get; set; } = "Pulse";

    private static string MutexName => $@"Local\{Name}.SingleInstance";
    private static string ActivateEventName => $@"Local\{Name}.Activate";

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
                return new SingleInstance(mutex, CreateActivateEvent());
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

    /// <summary>
    /// Before elevating: if a copy is already running, bring it to the front and say so, so a second launch
    /// never shows a UAC prompt just to focus a window. Takes nothing, so the elevated copy can start cleanly.
    /// </summary>
    public static bool SignalIfRunning()
    {
        bool running;
        try
        {
            running = Mutex.TryOpenExisting(MutexName, out var existing);
            existing?.Dispose();
        }
        catch (UnauthorizedAccessException)
        {
            running = true;   // held by an elevated copy
        }
        if (running)
            SignalRunningInstance();
        return running;
    }

    /// <summary>Waits for a copy that's starting (from the startup task) and asks it to show its window.</summary>
    public static bool WaitThenActivate(TimeSpan timeout)
    {
        var until = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < until)
        {
            if (SignalRunningInstance())
                return true;
            Thread.Sleep(200);
        }
        return false;
    }

    /// <summary>Calls <paramref name="onActivate"/> (on a thread-pool thread) whenever a second launch happens.</summary>
    public void ListenForActivation(Action onActivate)
    {
        if (_activate is null) return;
        _registration = ThreadPool.RegisterWaitForSingleObject(
            _activate, (_, _) => onActivate(), null, Timeout.Infinite, executeOnlyOnce: false);
    }

    private static bool SignalRunningInstance()
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(ActivateEventName, out var activate))
                return false;
            using (activate)
                activate.Set();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            // An event from a copy started before this change, without the permissive descriptor.
            return false;
        }
    }

    /// <summary>
    /// The activation event, signalable by this user at any integrity level: full access for the user, SYSTEM
    /// and administrators, and a Medium mandatory label with no-write-up, so a non-elevated launch may set it.
    /// Falls back to a default event if the descriptor can't be built.
    /// </summary>
    private static EventWaitHandle CreateActivateEvent()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var sddl = $"D:(A;;0x1F0003;;;{identity.User})(A;;0x1F0003;;;SY)(A;;0x1F0003;;;BA)S:(ML;;NW;;;ME)";
        if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(sddl, 1, out var descriptor, out _))
            return new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        try
        {
            var attributes = new SECURITY_ATTRIBUTES
            {
                nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
                lpSecurityDescriptor = descriptor,
            };
            var handle = CreateEventW(ref attributes, manualReset: false, initialState: false, ActivateEventName);
            if (handle == IntPtr.Zero)
                throw new Win32Exception(Marshal.GetLastWin32Error());
            return new EventWaitHandle(false, EventResetMode.AutoReset) { SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: true) };
        }
        catch (Win32Exception ex)
        {
            Log.Write($"Couldn't create the activation event with its descriptor ({ex.Message}); using a default one");
            return new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
        }
        finally
        {
            LocalFree(descriptor);
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

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string sddl, int revision, out IntPtr descriptor, out int size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(ref SECURITY_ATTRIBUTES attributes, bool manualReset, bool initialState, string name);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}
