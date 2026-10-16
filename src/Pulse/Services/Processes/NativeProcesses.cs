using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace Pulse.Services.Processes;

/// <summary>One process as Windows reports it at a moment: who it is and what it has used so far.</summary>
/// <param name="CreateTime">When it started (FILETIME ticks). With the PID, it tells a process apart from a later one that reuses the PID.</param>
/// <param name="CpuTime">Kernel plus user time used since it started, in 100 ns ticks.</param>
/// <param name="Memory">Private working set in bytes: what Task Manager's Memory column shows.</param>
public readonly record struct ProcessSample(int Pid, long CreateTime, string Name, int SessionId, long CpuTime, long Memory);

/// <summary>
/// Reads every process in one call to <c>NtQuerySystemInformation(SystemProcessInformation)</c>. Unlike
/// <see cref="System.Diagnostics.Process"/>, it opens no handle per process, so it's fast (a few ms for
/// 300 processes) and sees protected processes too. Also: a process's file path, and ending a process.
/// </summary>
internal static class NativeProcesses
{
    private const int SystemProcessInformation = 5;
    private const uint StatusInfoLengthMismatch = 0xC0000004;

    // SYSTEM_PROCESS_INFORMATION on x64 (Pulse is win-x64 only).
    private const int NextEntryOffset = 0;
    private const int WorkingSetPrivateSize = 8;
    private const int CreateTimeOffset = 32;
    private const int UserTime = 40;
    private const int KernelTime = 48;
    private const int ImageNameLength = 56;
    private const int ImageNameBuffer = 64;
    private const int UniqueProcessId = 80;
    private const int SessionIdOffset = 100;

    private static IntPtr _buffer;
    private static int _bufferSize;
    private static readonly object Gate = new();

    /// <summary>Image names repeat every sample; keep one string per name instead of allocating them each time.</summary>
    private static readonly Dictionary<string, string> Names = new(StringComparer.Ordinal);

    /// <summary>Every running process except the System Idle Process (PID 0, which is the CPU's idle time).</summary>
    public static List<ProcessSample> Snapshot()
    {
        lock (Gate)
        {
            if (_buffer == IntPtr.Zero)
                Grow(512 * 1024);

            while (true)
            {
                var status = NtQuerySystemInformation(SystemProcessInformation, _buffer, _bufferSize, out var needed);
                if (status == StatusInfoLengthMismatch)
                {
                    // Processes can start between the two calls; leave some room.
                    Grow(Math.Max(needed + 64 * 1024, _bufferSize * 2));
                    continue;
                }
                if (status != 0)
                    throw new Win32Exception($"NtQuerySystemInformation failed (0x{status:X8})");
                return Parse(_buffer);
            }
        }
    }

    private static void Grow(int size)
    {
        if (_buffer != IntPtr.Zero)
            Marshal.FreeHGlobal(_buffer);
        _buffer = Marshal.AllocHGlobal(size);
        _bufferSize = size;
    }

    private static List<ProcessSample> Parse(IntPtr buffer)
    {
        var result = new List<ProcessSample>(400);
        var entry = buffer;
        while (true)
        {
            var pid = (int)Marshal.ReadIntPtr(entry, UniqueProcessId);
            if (pid != 0)
            {
                var nameLength = Marshal.ReadInt16(entry, ImageNameLength) / 2;
                var namePointer = Marshal.ReadIntPtr(entry, ImageNameBuffer);
                result.Add(new ProcessSample(
                    pid,
                    Marshal.ReadInt64(entry, CreateTimeOffset),
                    Intern(namePointer, nameLength, pid),
                    Marshal.ReadInt32(entry, SessionIdOffset),
                    Marshal.ReadInt64(entry, UserTime) + Marshal.ReadInt64(entry, KernelTime),
                    Marshal.ReadInt64(entry, WorkingSetPrivateSize)));
            }

            var next = Marshal.ReadInt32(entry, NextEntryOffset);
            if (next == 0)
                return result;
            entry += next;
        }
    }

    private static string Intern(IntPtr pointer, int length, int pid)
    {
        if (pointer == IntPtr.Zero || length == 0)
            return pid == 4 ? "System" : $"Process {pid}";
        var name = Marshal.PtrToStringUni(pointer, length);
        if (Names.TryGetValue(name, out var known))
            return known;
        if (Names.Count > 4096)
            Names.Clear();
        Names[name] = name;
        return name;
    }

    /// <summary>The full path of a process's executable, or null when Windows won't say (protected processes).</summary>
    public static string? ImagePath(int pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            return null;
        try
        {
            var builder = new StringBuilder(1024);
            var size = builder.Capacity;
            return QueryFullProcessImageName(handle, 0, builder, ref size) ? builder.ToString(0, size) : null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    /// <summary>
    /// Ends a process, but only if it's still the one that was sampled: a PID can be reused by a new
    /// process after the old one exits, and that one must not be ended by mistake.
    /// </summary>
    /// <returns>True if it was ended (or had already exited); false if Windows refused.</returns>
    public static bool Terminate(int pid, long createTime)
    {
        var handle = OpenProcess(ProcessTerminate | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
            // Gone already counts as done; anything else (access denied) is a refusal.
            return Marshal.GetLastWin32Error() == ErrorInvalidParameter;
        try
        {
            if (!GetProcessTimes(handle, out var created, out _, out _, out _) || created != createTime)
                return true;   // a different process now has this PID: the one we meant has exited
            // Refused because it has just exited on its own: that's done too.
            return TerminateProcess(handle, 1)
                   || (GetExitCodeProcess(handle, out var code) && code != StillActive);
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private const uint ProcessTerminate = 0x0001;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const int ErrorInvalidParameter = 87;
    private const uint StillActive = 259;

    [DllImport("ntdll.dll")]
    private static extern uint NtQuerySystemInformation(int infoClass, IntPtr info, int length, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, int pid);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(IntPtr handle, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetExitCodeProcess(IntPtr handle, out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(IntPtr handle, out long creation, out long exit, out long kernel, out long user);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr handle, int flags, StringBuilder name, ref int size);
}
