using System.Diagnostics;

namespace Pulse.Services.Processes;

/// <summary>
/// Samples every process's CPU, GPU and memory use every <see cref="Interval"/> on its own background
/// thread, but only while something on screen shows it (<see cref="IsWanted"/>): the dashboard's Top
/// processes tile or the Processes page. Otherwise it sleeps and costs nothing.
///
/// It runs beside the sensor polling rather than inside it: reading processes never touches
/// LibreHardwareMonitor, and a slow USB device in the sensor loop shouldn't delay it.
/// </summary>
public sealed class ProcessMonitor : IDisposable
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);

    /// <summary>The first reading after waking is only a baseline, so the next comes sooner.</summary>
    private static readonly TimeSpan BaselineInterval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _cts = new();
    private readonly ManualResetEventSlim _wake = new(false);
    private readonly ProcessUsageTracker _tracker = new();
    private readonly AppInfoCache _info = new();
    private readonly SampleStats _stats = new();
    private readonly int _session = Process.GetCurrentProcess().SessionId;
    private readonly string _windowsFolder = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    private GpuEngineCounters? _gpu;
    private bool _gpuOpened;
    private Thread? _thread;
    private volatile bool _wanted;

    /// <summary>Raised on the sampling thread after each reading.</summary>
    public event Action<ProcessList>? Updated;

    public ProcessList? Latest { get; private set; }

    /// <summary>Sample while true; sleep while false. Set from the UI thread as pages and tiles come and go.</summary>
    public bool IsWanted
    {
        get => _wanted;
        set
        {
            if (_wanted == value) return;
            _wanted = value;
            _wake.Set();
            Log.Write(value ? "Process sampling started" : "Process sampling paused");
        }
    }

    /// <summary>A program's description and icon, once looked up (see <see cref="AppInfoCache"/>).</summary>
    public AppInfo? Info(string key) => _info.Find(key);

    public void Start()
    {
        if (_thread is not null) return;
        // STA: the icon lookup (SHGetFileInfo) is a shell call, which wants an apartment thread.
        _thread = new Thread(Run) { IsBackground = true, Name = "Pulse process sampler" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    /// <summary>Ends every process of a program (after the user confirmed). Returns how many Windows refused to end.</summary>
    public static int End(AppUsage app)
    {
        var refused = app.Members.Count(member => !NativeProcesses.Terminate(member.Pid, member.CreateTime));
        Log.Write($"End task: {app.ImageName}, {app.Count} process(es), {refused} refused");
        return refused;
    }

    private void Run()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!_wanted)
                {
                    _wake.Reset();
                    if (!_wanted)
                    {
                        // A pause makes the old baseline stale: start fresh when sampling resumes.
                        _tracker.Reset();
                        _wake.Wait(ct);
                        continue;
                    }
                }

                var baseline = !_tracker.HasBaseline;
                try
                {
                    Sample();
                }
                catch (Exception ex)
                {
                    Log.Write($"Process sampling failed: {ex.Message}");
                }
                ct.WaitHandle.WaitOne(baseline ? BaselineInterval : Interval);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Sample()
    {
        var started = Stopwatch.GetTimestamp();
        var samples = NativeProcesses.Snapshot();
        var cpuTime = Stopwatch.GetElapsedTime(started);

        if (!_gpuOpened)
        {
            _gpuOpened = true;
            _gpu = GpuEngineCounters.TryOpen();
            Log.Write(_gpu is null ? "GPU per-process counters aren't available" : "GPU per-process counters opened");
        }
        var gpuStarted = Stopwatch.GetTimestamp();
        var gpu = _gpu?.Read();
        var gpuTime = Stopwatch.GetElapsedTime(gpuStarted);

        var baseline = !_tracker.HasBaseline;
        var processes = _tracker.Update(samples, DateTime.UtcNow.ToFileTimeUtc(), Environment.ProcessorCount, gpu);
        var apps = ProcessApps.Group(processes, _session, Environment.ProcessId);
        // Task Manager's sections: apps have a window; Windows processes come from the Windows folder.
        _info.ResolvePaths(apps);
        var windows = DesktopWindows.Pids();
        apps = ProcessApps.Classify(apps, windows.Contains, _info.PathOf, _windowsFolder);
        _info.Resolve(apps);

        var list = new ProcessList(processes, apps, gpu is not null, baseline, DateTime.Now);
        Latest = list;
        _stats.Add(cpuTime, gpuTime, Stopwatch.GetElapsedTime(started), samples.Count);
        Updated?.Invoke(list);
    }

    /// <summary>Logs a once-a-minute summary of what sampling costs (the step's "under about 5 ms" target).</summary>
    private sealed class SampleStats
    {
        private DateTime _windowStart = DateTime.UtcNow;
        private double _cpu, _gpu, _total, _slowest;
        private int _count, _processes;

        public void Add(TimeSpan cpu, TimeSpan gpu, TimeSpan total, int processes)
        {
            _cpu += cpu.TotalMilliseconds;
            _gpu += gpu.TotalMilliseconds;
            _total += total.TotalMilliseconds;
            _slowest = Math.Max(_slowest, total.TotalMilliseconds);
            _processes = processes;
            if (++_count == 1)
                Log.Write($"Processes: first sample of {processes} took {total.TotalMilliseconds:0.0} ms "
                          + $"(processes {cpu.TotalMilliseconds:0.0} ms, GPU counters {gpu.TotalMilliseconds:0.0} ms)");

            if (DateTime.UtcNow - _windowStart < TimeSpan.FromMinutes(1))
                return;
            Log.Write($"Processes: {_count} samples of ~{_processes} in the last minute, average {_total / _count:0.0} ms "
                      + $"(processes {_cpu / _count:0.0} ms, GPU counters {_gpu / _count:0.0} ms), slowest {_slowest:0.0} ms");
            _windowStart = DateTime.UtcNow;
            _cpu = _gpu = _total = _slowest = 0;
            _count = 0;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _gpu?.Dispose();
        _cts.Dispose();
        _wake.Dispose();
    }
}
