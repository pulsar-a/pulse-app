using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Pulse.Services.Processes;

/// <summary>
/// GPU use per process from Windows' "GPU Engine" performance counters, the same data Task Manager shows.
/// Each counter instance is one process on one engine of one GPU, named like
/// <c>pid_1234_luid_0x00000000_0x0000D1F6_phys_0_eng_3_engtype_3D</c>.
///
/// Read through PDH with a wildcard, which picks up processes that start later. The counters are rates, so
/// the first read after opening has no values yet.
/// </summary>
internal sealed partial class GpuEngineCounters : IDisposable
{
    private const string CounterPath = @"\GPU Engine(*)\Utilization Percentage";
    private const uint PdhFmtDouble = 0x00000200;
    private const uint PdhMoreData = 0x800007D2;
    private const uint PdhNoData = 0x800007D5;
    private const uint PdhCstatusValidData = 0;
    private const uint PdhCstatusNewData = 1;

    private IntPtr _query;
    private IntPtr _counter;
    private IntPtr _buffer;
    private int _bufferSize;

    private GpuEngineCounters(IntPtr query, IntPtr counter)
    {
        _query = query;
        _counter = counter;
    }

    /// <summary>Opens the counters, or returns null on a machine without them (no WDDM 2 GPU, counters disabled).</summary>
    public static GpuEngineCounters? TryOpen()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0)
                return null;
            if (PdhAddEnglishCounter(query, CounterPath, IntPtr.Zero, out var counter) != 0)
            {
                PdhCloseQuery(query);
                return null;
            }
            var counters = new GpuEngineCounters(query, counter);
            counters.Read();   // the baseline for the first rate
            return counters;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return null;
        }
    }

    /// <summary>Utilization per PID since the previous read (0–100), or null if the counters couldn't be read.</summary>
    public Dictionary<int, double>? Read()
    {
        if (PdhCollectQueryData(_query) is var collected && collected != 0)
            return collected == PdhNoData ? new Dictionary<int, double>() : null;

        while (true)
        {
            var size = _bufferSize;
            var status = PdhGetFormattedCounterArray(_counter, PdhFmtDouble, ref size, out var count, _buffer);
            if (status == PdhMoreData)
            {
                if (_buffer != IntPtr.Zero) Marshal.FreeHGlobal(_buffer);
                _bufferSize = size + 4096;
                _buffer = Marshal.AllocHGlobal(_bufferSize);
                continue;
            }
            if (status == PdhNoData || count == 0)
                return new Dictionary<int, double>();
            if (status != 0)
                return null;
            return Aggregate(Items(_buffer, count));
        }
    }

    /// <summary>PDH_FMT_COUNTERVALUE_ITEM_W on x64: name pointer, then status (padded) and the double.</summary>
    private static IEnumerable<(string Instance, double Value)> Items(IntPtr buffer, int count)
    {
        const int itemSize = 24;
        for (var i = 0; i < count; i++)
        {
            var item = buffer + i * itemSize;
            var status = (uint)Marshal.ReadInt32(item, 8);
            if (status != PdhCstatusValidData && status != PdhCstatusNewData)
                continue;
            var name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item, 0));
            if (name is null)
                continue;
            yield return (name, BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, 16)));
        }
    }

    [GeneratedRegex(@"^pid_(\d+)_luid_(0x[0-9A-Fa-f]+_0x[0-9A-Fa-f]+)_phys_(\d+)_eng_(\d+)_engtype_", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceName();

    /// <summary>
    /// Per process, its busiest engine, as Task Manager counts it: a game at 80% of the 3D engine and 10% of
    /// the copy engine is at 80%. Instances for the same process and engine (rare) add up.
    /// </summary>
    internal static Dictionary<int, double> Aggregate(IEnumerable<(string Instance, double Value)> items)
    {
        var perEngine = new Dictionary<(int Pid, string Engine), double>();
        foreach (var (instance, value) in items)
        {
            var match = InstanceName().Match(instance);
            if (!match.Success || double.IsNaN(value) || value <= 0
                || !int.TryParse(match.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid))
                continue;
            var engine = $"{match.Groups[2].Value}/{match.Groups[3].Value}/{match.Groups[4].Value}";
            perEngine[(pid, engine)] = perEngine.GetValueOrDefault((pid, engine)) + value;
        }

        var result = new Dictionary<int, double>();
        foreach (var ((pid, _), value) in perEngine)
            result[pid] = Math.Min(100, Math.Max(result.GetValueOrDefault(pid), value));
        return result;
    }

    public void Dispose()
    {
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = _counter = IntPtr.Zero;
        }
        if (_buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_buffer);
            _buffer = IntPtr.Zero;
        }
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounter(IntPtr query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhGetFormattedCounterArray(IntPtr counter, uint format, ref int bufferSize, out int itemCount, IntPtr buffer);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}
