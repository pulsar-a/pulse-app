using System.Collections.ObjectModel;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Pulse.Services.Processes;

namespace Pulse.ViewModels;

/// <summary>Number formats for the process lists.</summary>
public static class ProcessFormat
{
    /// <summary>"0.4%" under 10%, "37%" above, like Task Manager's precision where it matters.</summary>
    public static string Percent(double? value) => value switch
    {
        null => "—",
        < 0.05 => "0%",
        < 10 => $"{value:0.0}%",
        _ => $"{value:0}%",
    };

    public static string Memory(long bytes)
    {
        var mb = bytes / 1024.0 / 1024.0;
        return mb >= 1024 ? $"{mb / 1024:0.0} GB" : $"{mb:0} MB";
    }

    public static string Count(int count) => count == 1 ? "" : $"{count} processes";
}

/// <summary>What a top list ranks by.</summary>
public enum ProcessMetric
{
    Cpu,
    Gpu,
    Memory,
}

/// <summary>One program in a top list on the dashboard tile.</summary>
public partial class TopProcessViewModel : ObservableObject
{
    [ObservableProperty] public partial string Name { get; set; } = "";
    [ObservableProperty] public partial ImageSource? Icon { get; set; }
    [ObservableProperty] public partial string ValueText { get; set; } = "";
    [ObservableProperty] public partial double Fraction { get; set; }
    [ObservableProperty] public partial string ToolTip { get; set; } = "";
}

/// <summary>One of the tile's columns: the top programs by CPU, by GPU, or by memory.</summary>
public partial class TopProcessListViewModel(ProcessMetric metric, string title) : ObservableObject
{
    public ProcessMetric Metric { get; } = metric;
    public string Title { get; } = title;
    public ObservableCollection<TopProcessViewModel> Items { get; } = [];

    /// <summary>The CPU or GPU this list is about is running hot: its heading takes the heat color.</summary>
    [ObservableProperty] public partial HeatLevel Heat { get; set; }

    public static double Value(AppUsage app, ProcessMetric metric) => metric switch
    {
        ProcessMetric.Cpu => app.Cpu,
        ProcessMetric.Gpu => app.Gpu ?? 0,
        _ => app.Memory,
    };

    public void Update(IReadOnlyList<AppUsage> apps, Func<string, AppInfo?> info, int count)
    {
        var top = apps.OrderByDescending(a => Value(a, Metric)).ThenBy(a => a.Key).Take(count).ToList();
        // Bars are relative to the busiest, so small numbers still read; for percentages never less than 10%
        // of full scale, so 0.3% doesn't look like a full bar.
        var scale = Math.Max(top.Count > 0 ? Value(top[0], Metric) : 0, Metric == ProcessMetric.Memory ? 1 : 10);

        while (Items.Count < top.Count) Items.Add(new TopProcessViewModel());
        while (Items.Count > top.Count) Items.RemoveAt(Items.Count - 1);
        for (var i = 0; i < top.Count; i++)
        {
            var app = top[i];
            var known = info(app.Key);
            var item = Items[i];
            item.Name = known?.DisplayName ?? app.ImageName;
            item.Icon = known?.Icon;
            item.ValueText = Metric == ProcessMetric.Memory ? ProcessFormat.Memory(app.Memory) : ProcessFormat.Percent(Value(app, Metric));
            item.Fraction = Math.Clamp(Value(app, Metric) / scale, 0, 1);
            item.ToolTip = app.Count > 1 ? $"{app.ImageName} · {app.Count} processes" : app.ImageName;
        }
    }
}

/// <summary>
/// The dashboard's Top processes tile: the busiest programs by CPU, GPU and memory. When the processor
/// (or graphics card) runs warm, the tile takes the heat color and asks "What's heating your CPU".
/// </summary>
public partial class ProcessesCardViewModel : ObservableObject
{
    public const int TopCount = 5;

    private ProcessList? _latest;
    private Func<string, AppInfo?> _info = _ => null;
    private HeatLevel _cpuHeat, _gpuHeat;
    private string _cpuReading = "", _gpuReading = "";

    public TopProcessListViewModel Cpu { get; } = new(ProcessMetric.Cpu, "Processor");
    public TopProcessListViewModel Gpu { get; } = new(ProcessMetric.Gpu, "Graphics");
    public TopProcessListViewModel Memory { get; } = new(ProcessMetric.Memory, "Memory");

    /// <summary>The columns in order of importance; the tile shows as many as fit.</summary>
    public ObservableCollection<TopProcessListViewModel> Lists { get; } = [];

    public ProcessesCardViewModel() => Lists.Add(Cpu);

    [ObservableProperty] public partial string Title { get; private set; } = "Top processes";
    [ObservableProperty] public partial string Subtitle { get; private set; } = "Measuring…";
    [ObservableProperty] public partial bool HasData { get; private set; }
    [ObservableProperty] public partial HeatLevel Heat { get; private set; }

    public void Apply(ProcessList list, Func<string, AppInfo?> info)
    {
        _info = info;
        // The first reading after waking has no CPU figures yet; keep showing the last real one.
        if (list.IsBaseline && HasData)
            return;
        _latest = list;

        TopProcessListViewModel[] lists = list.HasGpu ? [Cpu, Gpu, Memory] : [Cpu, Memory];
        if (!Lists.SequenceEqual(lists))
        {
            Lists.Clear();
            foreach (var l in lists) Lists.Add(l);
        }
        foreach (var l in lists)
            l.Update(list.Apps, info, TopCount);

        HasData = !list.IsBaseline;
        UpdateTitle();
    }

    /// <summary>From the Processor and Graphics cards on each reading, with their headline temperature.</summary>
    public void SetHeat(HeatLevel cpu, string cpuReading, HeatLevel gpu, string gpuReading)
    {
        if (cpu == _cpuHeat && gpu == _gpuHeat && cpuReading == _cpuReading && gpuReading == _gpuReading)
            return;
        (_cpuHeat, _cpuReading, _gpuHeat, _gpuReading) = (cpu, cpuReading, gpu, gpuReading);
        UpdateTitle();
    }

    private void UpdateTitle()
    {
        var gpuShown = _latest?.HasGpu == true;
        var gpuHeat = gpuShown ? _gpuHeat : HeatLevel.None;
        Cpu.Heat = _cpuHeat;
        Gpu.Heat = gpuHeat;
        Heat = _cpuHeat > gpuHeat ? _cpuHeat : gpuHeat;

        // The CPU first: the plan's main question, and the one processes answer best.
        if (_cpuHeat >= HeatLevel.Warm)
        {
            Title = "What's heating your CPU";
            Subtitle = $"Processor at {_cpuReading}";
        }
        else if (gpuHeat >= HeatLevel.Warm)
        {
            Title = "What's heating your GPU";
            Subtitle = $"Graphics card at {_gpuReading}";
        }
        else
        {
            Title = "Top processes";
            Subtitle = _latest is null || !HasData ? "Measuring…" : $"{_latest.Processes.Count} processes running";
        }
    }
}

/// <summary>One program on the Processes page.</summary>
public partial class ProcessRowViewModel(string key) : ObservableObject
{
    public string Key { get; } = key;

    /// <summary>The latest reading; End task ends its members, including ones that started since it was shown.</summary>
    public AppUsage? App { get; private set; }

    [ObservableProperty] public partial string Name { get; private set; } = "";
    [ObservableProperty] public partial string Detail { get; private set; } = "";
    [ObservableProperty] public partial ImageSource? Icon { get; private set; }
    [ObservableProperty] public partial string CpuText { get; private set; } = "";
    [ObservableProperty] public partial string GpuText { get; private set; } = "";
    [ObservableProperty] public partial string MemoryText { get; private set; } = "";
    [ObservableProperty] public partial double CpuFraction { get; private set; }
    [ObservableProperty] public partial bool CanEnd { get; private set; }

    public void Update(AppUsage app, AppInfo? info)
    {
        App = app;
        Name = info?.DisplayName ?? app.ImageName;
        var count = ProcessFormat.Count(app.Count);
        Detail = count.Length == 0 ? app.ImageName : $"{app.ImageName} · {count}";
        Icon = info?.Icon;
        CpuText = ProcessFormat.Percent(app.Cpu);
        GpuText = ProcessFormat.Percent(app.Gpu);
        MemoryText = ProcessFormat.Memory(app.Memory);
        CpuFraction = Math.Clamp(app.Cpu / 100, 0, 1);
        CanEnd = app.CanEnd;
    }
}

public enum ProcessSort
{
    Name,
    Cpu,
    Gpu,
    Memory,
}

/// <summary>
/// The Processes page: every program with its CPU, GPU and memory use, sortable and searchable, with
/// End task for the user's own programs (after a confirmation).
/// </summary>
public partial class ProcessesViewModel : ObservableObject
{
    private readonly Func<string, AppInfo?> _info;
    private readonly Func<AppUsage, int> _end;
    private readonly Dictionary<string, ProcessRowViewModel> _rows = new();
    private ProcessList? _latest;

    /// <param name="info">A program's description and icon, once known.</param>
    /// <param name="end">Ends a program's processes; returns how many Windows refused.</param>
    public ProcessesViewModel(Func<string, AppInfo?> info, Func<AppUsage, int> end)
    {
        _info = info;
        _end = end;
    }

    public ObservableCollection<ProcessRowViewModel> Rows { get; } = [];

    /// <summary>Asks the user before ending a program; set by the view. Without it nothing is ever ended.</summary>
    public Func<ProcessRowViewModel, Task<bool>>? Confirm { get; set; }

    /// <summary>True while the page is on screen: only then do rows update.</summary>
    public bool IsActive
    {
        get => _isActive;
        set
        {
            if (_isActive == value) return;
            _isActive = value;
            if (value && _latest is not null) Refresh();
        }
    }

    private bool _isActive;

    [ObservableProperty] public partial string Query { get; set; } = "";
    [ObservableProperty] public partial ProcessSort Sort { get; private set; } = ProcessSort.Cpu;
    [ObservableProperty] public partial bool SortDescending { get; private set; } = true;
    [ObservableProperty] public partial bool HasGpu { get; private set; }
    [ObservableProperty] public partial bool HasData { get; private set; }
    [ObservableProperty] public partial bool HasNoMatches { get; private set; }
    [ObservableProperty] public partial string Summary { get; private set; } = "Measuring…";

    /// <summary>What happened after End task, shown under the header until the next one.</summary>
    [ObservableProperty] public partial string? Message { get; private set; }

    public string NameArrow => Arrow(ProcessSort.Name);
    public string CpuArrow => Arrow(ProcessSort.Cpu);
    public string GpuArrow => Arrow(ProcessSort.Gpu);
    public string MemoryArrow => Arrow(ProcessSort.Memory);

    private string Arrow(ProcessSort column) => Sort != column ? "" : SortDescending ? " ▾" : " ▴";

    partial void OnQueryChanged(string value) => Refresh();

    /// <summary>A column header: sorts by it, or flips the order when it's already the sort. Numbers start busiest first.</summary>
    [RelayCommand]
    private void SortBy(ProcessSort column)
    {
        if (Sort == column)
            SortDescending = !SortDescending;
        else
        {
            Sort = column;
            SortDescending = column != ProcessSort.Name;
        }
        OnPropertyChanged(nameof(NameArrow));
        OnPropertyChanged(nameof(CpuArrow));
        OnPropertyChanged(nameof(GpuArrow));
        OnPropertyChanged(nameof(MemoryArrow));
        Refresh();
    }

    [RelayCommand]
    private async Task EndTask(ProcessRowViewModel? row)
    {
        if (row?.App is not { CanEnd: true } || Confirm is null || !await Confirm(row))
            return;
        // The members as of now: the row may have updated while the confirmation was open.
        if (row.App is not { CanEnd: true } app)
            return;

        var refused = _end(app);
        Message = refused == 0 ? $"Ended {row.Name}."
            : refused == app.Count ? $"Windows wouldn't end {row.Name}."
            : $"Ended {row.Name}, but Windows wouldn't end {refused} of its {app.Count} processes.";
    }

    public void Apply(ProcessList list)
    {
        if (list.IsBaseline && HasData)
            return;
        _latest = list;
        if (_isActive)
            Refresh();
    }

    private void Refresh()
    {
        if (_latest is not { } list)
            return;

        HasGpu = list.HasGpu;
        HasData = !list.IsBaseline;
        Summary = HasData ? $"{list.Processes.Count} processes · {list.Apps.Count} programs" : "Measuring…";

        var query = Query.Trim();
        var shown = new List<ProcessRowViewModel>();
        var seen = new HashSet<string>();
        foreach (var app in list.Apps)
        {
            seen.Add(app.Key);
            if (!_rows.TryGetValue(app.Key, out var row))
                _rows[app.Key] = row = new ProcessRowViewModel(app.Key);
            row.Update(app, _info(app.Key));
            if (query.Length == 0 || row.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                || app.ImageName.Contains(query, StringComparison.OrdinalIgnoreCase))
                shown.Add(row);
        }
        foreach (var gone in _rows.Keys.Where(k => !seen.Contains(k)).ToList())
            _rows.Remove(gone);

        shown.Sort(Compare);
        HasNoMatches = HasData && shown.Count == 0;
        SyncRows(shown);
    }

    private int Compare(ProcessRowViewModel a, ProcessRowViewModel b)
    {
        var x = a.App!;
        var y = b.App!;
        var order = Sort switch
        {
            ProcessSort.Cpu => x.Cpu.CompareTo(y.Cpu),
            ProcessSort.Gpu => (x.Gpu ?? 0).CompareTo(y.Gpu ?? 0),
            ProcessSort.Memory => x.Memory.CompareTo(y.Memory),
            _ => ByName(a, b),
        };
        if (SortDescending) order = -order;
        // Ties (lots of programs at 0%) keep a steady order, so rows don't shuffle every update.
        return order != 0 ? order : ByName(a, b);
    }

    private static int ByName(ProcessRowViewModel a, ProcessRowViewModel b) =>
        string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase) is var byName && byName != 0
            ? byName
            : string.CompareOrdinal(a.Key, b.Key);

    /// <summary>Moves rows into place rather than rebuilding the list, so the selection and scroll position survive.</summary>
    private void SyncRows(List<ProcessRowViewModel> wanted)
    {
        var keep = wanted.ToHashSet();
        for (var i = Rows.Count - 1; i >= 0; i--)
        {
            if (!keep.Contains(Rows[i]))
                Rows.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = Rows.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) Rows.Insert(i, wanted[i]);
            else Rows.Move(at, i);
        }
    }
}
