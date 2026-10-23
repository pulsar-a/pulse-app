using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Pulse.Services.Coolers;
using Wpf.Ui.Controls;
using Color = System.Windows.Media.Color;

namespace Pulse.ViewModels;

/// <summary>What other parts of Pulse need to pin sensors to the dashboard.</summary>
public interface IPinBoard
{
    bool IsPinned(string sensorId);

    /// <summary>Pinned tiles a sensor can be added to, as (tile id, title).</summary>
    IReadOnlyList<(string Id, string Title)> PinTargets();

    void Pin(string sensorId, string? intoTileId = null);
    void Unpin(string sensorId);
}

public partial class DashboardViewModel : ObservableObject, IPinBoard
{
    private static readonly Regex CoreTemperature = new(@"^(P-Core|E-Core|Core|CPU Core) #(\d+)$");
    private static readonly Regex CoreLoad = new(@"^CPU Core #(\d+)( Thread #\d+)?$");
    private static readonly Regex PsuCapacity = new(@"(\d{3,4})");
    private static readonly Regex DimmTemperature = new(@"^DIMM #\d+$");
    private static readonly Regex DimmSlot = new(@"\s*\(#\d+\)$");
    private static readonly Regex UnnamedInput = new(@"^(Temperature|Voltage) #\d+$");
    private static readonly Regex SupplyRail = new(@"^(Vcore|CPU VCore|\+12V|\+5V|\+3\.3V)$", RegexOptions.IgnoreCase);

    private readonly bool _driverInstalled;
    private readonly bool _isElevated = HardwareService.IsElevated;
    private readonly SensorCatalog _catalog;
    private readonly SensorStream _stream;
    private readonly SettingsService _settings;
    private readonly Dictionary<string, DashboardTileViewModel> _tiles = new();

    public DashboardViewModel(bool driverInstalled, SensorCatalog catalog, SensorStream stream)
    {
        _driverInstalled = driverInstalled;
        _catalog = catalog;
        _stream = stream;
        _settings = catalog.Settings;
        Cards = [Cpu, Gpu, Memory, Power];
        Cooling.PumpChosen = id =>
        {
            if (id == PumpSensorId) return;
            if (id.Length == 0) ClearPump();
            else SetPump(id);
        };
        Cooling.UseSuggested = () =>
        {
            if (PumpWatch?.Change?.SuggestedId is { } id) PumpWatch.Use(id);
        };
        Cooling.KeepPump = () => PumpWatch?.Confirm();

        // Every layout change (here, from the details panel, or "Reset" in Settings) goes through settings.
        SyncTiles();
        _settings.Changed += SyncTiles;
    }

    /// <summary>Opens the sensor details panel; set by the main view model.</summary>
    public Action<string>? OpenDetails { get; init; }

    [RelayCommand]
    private void ShowDetails(string? sensorId)
    {
        if (sensorId is { Length: > 0 })
            OpenDetails?.Invoke(sensorId);
    }

    /// <summary>The polling interval; sparklines show a minute's worth of samples at this rate.</summary>
    public TimeSpan SampleInterval { get; set; } = TimeSpan.FromSeconds(1);

    private int HistorySamples => Math.Clamp((int)Math.Round(60 / SampleInterval.TotalSeconds), 12, 600);

    /// <summary>The last minute of a sensor from the stream (empty if there's no such sensor).</summary>
    private double[] History(SensorReading? sensor) =>
        sensor is null ? [] : _stream.Recent(sensor.Id, HistorySamples);

    /// <summary>Element-wise sum of two series, for derived readings like CPU + GPU power. A gap in both stays a gap.</summary>
    private static double[] Sum(double[] a, double[] b)
    {
        var length = Math.Max(a.Length, b.Length);
        var result = new double[length];
        for (var i = 0; i < length; i++)
        {
            // Align the ends: both series end at the latest sample.
            var x = i - (length - a.Length) is var ia && ia >= 0 ? a[ia] : double.NaN;
            var y = i - (length - b.Length) is var ib && ib >= 0 ? b[ib] : double.NaN;
            result[i] = double.IsNaN(x) && double.IsNaN(y) ? double.NaN
                : (double.IsNaN(x) ? 0 : x) + (double.IsNaN(y) ? 0 : y);
        }
        return result;
    }

    private string? PumpSensorId => _catalog.RoleSensor(SensorRoles.Pump);

    /// <summary>Notices when the cooling changed since Pulse last ran; set by the main view model with the alerts.</summary>
    public PumpWatch? PumpWatch { get; set; }

    private IReadOnlyList<AlertRule> Rules => _settings.Current.Alerts.Rules;

    /// <summary>Remembers which fan header the pump is on; the next tick moves it into the cooling card.</summary>
    private void SetPump(string sensorId) => _catalog.SetRole(SensorRoles.Pump, sensorId);

    /// <summary>
    /// "Not on a motherboard header". The role is cleared rather than forgotten, meaning "the user chose",
    /// so a lone header isn't automatically picked again.
    /// </summary>
    private void ClearPump() => _catalog.ClearRole(SensorRoles.Pump);

    private const string NoHeaderLabel = "Not on a motherboard header";

    public GaugeCardViewModel Cpu { get; } = new("Processor", SymbolRegular.DeveloperBoard24, Palette.Cpu);
    public GaugeCardViewModel Gpu { get; } = new("Graphics", SymbolRegular.DesktopPulse24, Palette.Gpu);
    public GaugeCardViewModel Memory { get; } = new("Memory", SymbolRegular.Ram20, Palette.Memory);
    public GaugeCardViewModel Power { get; } = new("System power", SymbolRegular.Flash24, Palette.Power);
    public IReadOnlyList<GaugeCardViewModel> Cards { get; }

    public CoresCardViewModel CoresCard { get; } = new();
    public FansCardViewModel FansCard { get; } = new();
    public DrivesCardViewModel DrivesCard { get; } = new();
    public NetworkViewModel Network { get; } = new();
    public CoolingViewModel Cooling { get; } = new();
    public ProcessesCardViewModel ProcessesCard { get; } = new();
    public CpuLoadViewModel CpuLoad { get; } = new();
    public MotherboardViewModel Motherboard { get; } = new();

    /// <summary>What Pulse is doing with each USB cooler it supports (set by the main view model).</summary>
    public IReadOnlyList<CoolerStatus> CoolerStatuses { get; set; } = [];

    /// <summary>The Top processes tile is on the dashboard (not hidden): processes are sampled only then.</summary>
    [ObservableProperty] public partial bool ShowsProcesses { get; private set; }

    /// <summary>Opens the Processes page; set by the main view model.</summary>
    public Action? OpenProcesses { get; set; }

    [RelayCommand]
    private void ShowProcesses() => OpenProcesses?.Invoke();

    public ObservableCollection<CoreTileViewModel> Cores => CoresCard.Cores;
    public ObservableCollection<FanRowViewModel> Fans => FansCard.Rows;
    public ObservableCollection<DriveRowViewModel> Drives => DrivesCard.Rows;
    public string CoresTitle => CoresCard.Title;

    /// <summary>True until the first snapshot arrives; the view shows skeleton cards meanwhile.</summary>
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;

    public void Apply(HardwareSnapshot snapshot)
    {
        var cpu = snapshot.OfType(HardwareType.Cpu).FirstOrDefault();
        var gpu = snapshot.OfType(HardwareType.GpuNvidia, HardwareType.GpuAmd).FirstOrDefault()
                  ?? snapshot.OfType(HardwareType.GpuIntel).FirstOrDefault();

        UpdateCpu(cpu);
        UpdateGpu(gpu);
        UpdateMemory(snapshot);
        UpdatePower(snapshot, cpu, gpu);
        UpdateCores(cpu);
        UpdateCpuLoad(cpu);
        UpdateCooling(snapshot, cpu);
        UpdateMotherboard(snapshot);
        UpdateFans(snapshot);
        UpdateDrives(snapshot);
        UpdateNetwork(snapshot);
        UpdatePinned(snapshot);
        ProcessesCard.SetHeat(Cpu.Heat, Cpu.GaugeText, Gpu.Heat, Gpu.GaugeText);
        IsLoading = false;
    }

    private void UpdateCpu(HardwareReading? cpu)
    {
        if (cpu is null) return;

        var tempSensor = cpu.First(SensorType.Temperature, "CPU Package", "Core Max", "Core Average", "Core (Tctl/Tdie)");
        var loadSensor = cpu.First(SensorType.Load, "CPU Total");
        var temp = tempSensor?.Value;
        var load = loadSensor?.Value;
        var power = CpuPower(cpu);

        // On hybrid CPUs the P-cores set the headline clock; otherwise use any core.
        var clocks = cpu.Sensors.Where(s => s.Type == SensorType.Clock && s.HasValue).ToList();
        var coreClocks = clocks.Where(s => s.Name.StartsWith("P-Core")).ToList();
        if (coreClocks.Count == 0)
            coreClocks = clocks.Where(s => s.Name.Contains("Core")).ToList();
        float? clock = coreClocks.Count > 0 ? coreClocks.Max(s => s.Value) : null;

        Cpu.Device = cpu.Name;
        if (temp is { } t)
        {
            Cpu.SetGauge("temperature", t, 100, v => SensorFormat.Degrees(v));
            Cpu.Heat = Heat.Above(t, Heat.Limit(Rules, Heat.CpuRule), Cpu.Heat);
            Cpu.GaugeCaption = "package";
            Cpu.HistoryMax = 100;
            Cpu.HistoryLabel = "Temperature, last minute";
            Cpu.SetHistory(History(tempSensor), HistorySamples);
            Cpu.SensorId = tempSensor?.Id;
        }
        else
        {
            Cpu.SetGauge("load", load, 100, v => $"{v:0}%");
            Cpu.Heat = HeatLevel.None;
            Cpu.GaugeCaption = "load";
            Cpu.HistoryMax = 100;
            Cpu.HistoryLabel = "Load, last minute";
            Cpu.SetHistory(History(loadSensor), HistorySamples);
            Cpu.SensorId = loadSensor?.Id;
        }

        Cpu.SetRows(
            ("Load", SensorFormat.Format(SensorType.Load, load)),
            ("Power", SensorFormat.Format(SensorType.Power, power)),
            (coreClocks.Any(s => s.Name.StartsWith("P-Core")) ? "P-core clock" : "Clock",
                SensorFormat.Format(SensorType.Clock, clock)));
    }

    private void UpdateGpu(HardwareReading? gpu)
    {
        if (gpu is null) return;

        var tempSensor = gpu.First(SensorType.Temperature, "GPU Core");
        var temp = tempSensor?.Value;
        Gpu.SensorId = tempSensor?.Id;
        var hotSpot = gpu.Value(SensorType.Temperature, "GPU Hot Spot");
        var memJunction = gpu.Value(SensorType.Temperature, "GPU Memory Junction");
        var loadSensor = gpu.First(SensorType.Load, "GPU Core");
        var load = loadSensor?.Value;
        var power = gpu.Value(SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        var clock = gpu.Value(SensorType.Clock, "GPU Core");
        var vramUsed = gpu.Value(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var vramTotal = gpu.Value(SensorType.SmallData, "GPU Memory Total");

        Gpu.Device = gpu.Name;
        Gpu.SetGauge("temperature", temp, 100, v => SensorFormat.Degrees(v));
        // The hot spot is what the GPU alert watches; without one, the core temperature stands in.
        Gpu.Heat = Heat.Above(hotSpot ?? temp, Heat.Limit(Rules, Heat.GpuRule), Gpu.Heat);
        Gpu.GaugeCaption = hotSpot is { } h ? $"hot spot {SensorFormat.Degrees(h)}" : "core";
        Gpu.HistoryMax = 100;
        Gpu.HistoryLabel = "Load, last minute";
        Gpu.SetHistory(History(loadSensor), HistorySamples);

        var vram = vramUsed is { } u && vramTotal is { } total
            ? $"{u / 1024:0.0} / {total / 1024:0} GB"
            : SensorFormat.Format(SensorType.SmallData, vramUsed);

        var rows = new List<(string, string)>
        {
            ("Load", SensorFormat.Format(SensorType.Load, load)),
            ("Power", SensorFormat.Format(SensorType.Power, power)),
            ("Core clock", SensorFormat.Format(SensorType.Clock, clock)),
            ("VRAM", vram),
        };
        if (memJunction is not null)
            rows.Add(("Memory junction", SensorFormat.Format(SensorType.Temperature, memJunction)));
        Gpu.SetRows(rows.ToArray());
    }

    private void UpdateMemory(HardwareSnapshot snapshot)
    {
        var memory = snapshot.OfType(HardwareType.Memory).ToList();
        var ram = memory.FirstOrDefault(h => h.Id == "/ram")
                  ?? memory.FirstOrDefault(h => !h.Name.Contains("Virtual"));
        if (ram is null) return;
        var virt = memory.FirstOrDefault(h => h.Id == "/vram" || h.Name.Contains("Virtual"));

        var loadSensor = ram.First(SensorType.Load, "Memory");
        var load = loadSensor?.Value;
        var used = ram.Value(SensorType.Data, "Memory Used");
        var available = ram.Value(SensorType.Data, "Memory Available");
        var total = used + available;

        // DIMM temperatures come from SPD sensors, which need the PawnIO driver. Each stick also reports
        // its thermal limits as temperature sensors ("Thermal Sensor Critical High Limit"), so take only
        // the actual readings, named "DIMM #n".
        var dimms = memory.Where(h => h.Id.StartsWith("/memory/dimm/")).ToList();
        var dimmTemps = dimms.SelectMany(h => h.Sensors)
            .Where(s => s.Type == SensorType.Temperature && s.HasValue && DimmTemperature.IsMatch(s.Name))
            .Select(s => s.Value!.Value)
            .ToList();

        // "Kingston - KF560C36-32 (#1)" → "Kingston KF560C36-32"
        var models = dimms.Select(h => DimmSlot.Replace(h.Name, "").Replace(" - ", " ").Trim()).Distinct().ToList();
        Memory.Device = models.Count == 1
            ? $"{dimms.Count} × {models[0]}"
            : total is { } tot ? $"{Math.Round(tot):0} GB installed" : "System memory";
        Memory.SetGauge("load", load, 100, v => $"{v:0}%");
        Memory.GaugeCaption = used is { } u && total is { } t ? $"{u:0.0} / {t:0} GB" : "used";
        Memory.HistoryMax = 100;
        Memory.HistoryLabel = "Usage, last minute";
        Memory.SetHistory(History(loadSensor), HistorySamples);
        Memory.SensorId = loadSensor?.Id;

        var rows = new List<(string, string)>
        {
            ("Used", SensorFormat.Format(SensorType.Data, used)),
            ("Available", SensorFormat.Format(SensorType.Data, available)),
            ("Virtual memory", SensorFormat.Format(SensorType.Load, virt?.Value(SensorType.Load, "Memory"))),
        };
        if (dimmTemps.Count > 0)
            rows.Add(("Hottest DIMM", SensorFormat.Format(SensorType.Temperature, dimmTemps.Max())));
        Memory.SetRows(rows.ToArray());
    }

    private void UpdatePower(HardwareSnapshot snapshot, HardwareReading? cpu, HardwareReading? gpu)
    {
        var cpuPower = cpu is null ? null : CpuPower(cpu);
        var gpuPowerSensor = gpu?.First(SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        var gpuPower = gpuPowerSensor?.Value;
        var psu = snapshot.OfType(HardwareType.Psu).FirstOrDefault();

        if (psu is not null)
        {
            var totalSensor = psu.First(SensorType.Power, "Total watts", "Total Output");
            var total = totalSensor?.Value;
            var output = psu.Value(SensorType.Power, "Total Output");
            var capacity = PsuCapacity.Match(psu.Name) is { Success: true } m ? float.Parse(m.Value) : 1000f;
            var psuTemp = psu.AllSensors.Where(s => s.Type == SensorType.Temperature && s.HasValue)
                .Select(s => s.Value).Max();

            Power.Device = psu.Name;
            Power.SetGauge("psu", total, capacity, v => $"{v:0} W");
            // No alert for this one: warm from 75% of the PSU's rating, hot from 90%.
            Power.Heat = Heat.Above(total / capacity * 100, 90, Power.Heat, band: 15);
            Power.GaugeCaption = $"of {capacity:0} W";
            Power.HistoryMax = double.NaN;
            Power.HistoryLabel = "System draw, last minute";
            Power.SetHistory(History(totalSensor), HistorySamples);
            Power.SensorId = totalSensor?.Id;
            Power.SetRows(
                ("CPU package", SensorFormat.Format(SensorType.Power, cpuPower)),
                ("Graphics card", SensorFormat.Format(SensorType.Power, gpuPower)),
                ("PSU output", SensorFormat.Format(SensorType.Power, output)),
                ("PSU temperature", SensorFormat.Format(SensorType.Temperature, psuTemp)));
            return;
        }

        float? sum = cpuPower is null && gpuPower is null ? null : (cpuPower ?? 0) + (gpuPower ?? 0);
        Power.Device = "CPU and graphics card";
        Power.SetGauge("cpu+gpu", sum, 800, v => $"{v:0} W");
        Power.Heat = HeatLevel.None;
        Power.GaugeCaption = "CPU + GPU";
        Power.HistoryMax = double.NaN;
        Power.HistoryLabel = "Draw, last minute";
        var cpuPowerSensor = cpu?.First(SensorType.Power, "CPU Package", "Package");
        Power.SetHistory(Sum(History(cpuPowerSensor), History(gpuPowerSensor)), HistorySamples);
        Power.SensorId = cpuPowerSensor?.Id ?? gpuPowerSensor?.Id;
        Power.SetRows(
            ("CPU package", SensorFormat.Format(SensorType.Power, cpuPower)),
            ("Graphics card", SensorFormat.Format(SensorType.Power, gpuPower)));
    }

    private void UpdateCores(HardwareReading? cpu)
    {
        if (cpu is null) return;

        var temps = cpu.Sensors
            .Where(s => s.Type == SensorType.Temperature && CoreTemperature.IsMatch(s.Name))
            .ToList();

        List<(string Label, string Name, float? Value, string Id)> tiles;
        bool showingTemps;

        if (temps.Any(s => s.HasValue))
        {
            showingTemps = true;
            tiles = temps.Select(s => (ShortCoreLabel(s.Name), _catalog.DisplayName(s), s.Value, s.Id)).ToList();
            var pCores = temps.Count(s => s.Name.StartsWith("P-Core"));
            var eCores = temps.Count(s => s.Name.StartsWith("E-Core"));
            var hottest = temps.Where(s => s.HasValue).MaxBy(s => s.Value)!;
            CoresCard.Title = "Core temperatures";
            CoresCard.Subtitle = (pCores > 0 && eCores > 0 ? $"{pCores} P-cores, {eCores} E-cores · " : $"{temps.Count} cores · ")
                            + $"hottest {_catalog.DisplayName(hottest)} at {SensorFormat.WholeTemperature(hottest.Value!.Value)}";
        }
        else
        {
            // Without the driver there are no temperatures, but Windows still reports per-core load.
            showingTemps = false;
            tiles = cpu.Sensors
                .Where(s => s.Type == SensorType.Load && CoreLoad.IsMatch(s.Name))
                .GroupBy(s => int.Parse(CoreLoad.Match(s.Name).Groups[1].Value))
                .OrderBy(g => g.Key)
                .Select(g => ($"{g.Key}", $"Core #{g.Key}", g.Max(s => s.Value), g.MaxBy(s => s.Value)!.Id))
                .ToList();
            CoresCard.Title = "Core load";
            CoresCard.Subtitle = _driverInstalled
                ? $"{tiles.Count} cores · temperatures unavailable"
                : $"{tiles.Count} cores · install PawnIO to see temperatures";
        }

        if (Cores.Count != tiles.Count)
        {
            Cores.Clear();
            foreach (var _ in tiles) Cores.Add(new CoreTileViewModel());
        }

        for (var i = 0; i < tiles.Count; i++)
        {
            var (label, name, value, id) = tiles[i];
            var tile = Cores[i];
            tile.Label = label;
            tile.SensorId = id;
            if (value is not { } v || float.IsNaN(v))
            {
                tile.ValueText = "";
                tile.Fraction = 0;
                tile.ToolTip = $"{name}: no reading";
                continue;
            }

            if (showingTemps)
            {
                tile.ValueText = $"{SensorFormat.ToDisplayTemperature(v):0}";
                tile.Fraction = Math.Clamp((v - 20) / 80.0, 0.04, 1);
                tile.Fill = Palette.Temperature(v);
                tile.ToolTip = $"{name}: {SensorFormat.Format(SensorType.Temperature, v)}";
            }
            else
            {
                tile.ValueText = $"{v:0}";
                tile.Fraction = Math.Clamp(v / 100.0, 0.04, 1);
                tile.Fill = Palette.Heat(v / 100.0);
                tile.ToolTip = $"{name}: {v:0.0} % load";
            }
        }
    }

    private void UpdateCpuLoad(HardwareReading? cpu)
    {
        if (cpu is null) return;

        var totalSensor = cpu.First(SensorType.Load, "CPU Total");
        var busiestSensor = cpu.First(SensorType.Load, "CPU Core Max");
        var threads = cpu.Sensors.Where(s => s.Type == SensorType.Load && CoreLoad.IsMatch(s.Name)).ToList();
        var cores = threads.Select(s => CoreLoad.Match(s.Name).Groups[1].Value).Distinct().Count();
        // Without a "CPU Core Max" sensor, the busiest thread stands in (with no history).
        var busiest = busiestSensor?.Value ?? threads.Where(s => s.HasValue).Select(s => s.Value).Max();

        CpuLoad.Device = cpu.Name;
        CpuLoad.SensorId = totalSensor?.Id;
        CpuLoad.TotalText = totalSensor?.Value is { } total ? $"{total:0}%" : SensorFormat.Empty;
        CpuLoad.BusiestText = busiest is { } b ? $"{b:0}%" : SensorFormat.Empty;
        CpuLoad.Details = cores == 0 ? ""
            : threads.Count > cores ? $"{cores} cores · {threads.Count} threads"
            : $"{cores} cores";
        CpuLoad.SetHistory(History(totalSensor), History(busiestSensor), HistorySamples);
    }

    /// <summary>
    /// Package power, or null when it can't be read. Without the driver LHM reports 0 W,
    /// which no running CPU actually draws.
    /// </summary>
    private static float? CpuPower(HardwareReading cpu) =>
        cpu.Value(SensorType.Power, "CPU Package", "Package") is > 0 and var p ? p : null;

    private static string ShortCoreLabel(string name)
    {
        var m = CoreTemperature.Match(name);
        return m.Groups[1].Value switch
        {
            "P-Core" => $"P{m.Groups[2].Value}",
            "E-Core" => $"E{m.Groups[2].Value}",
            _ => m.Groups[2].Value,
        };
    }

    private void UpdateFans(HardwareSnapshot snapshot)
    {
        var fans = new List<(string Id, string Name, string Source, float Rpm)>();
        foreach (var hardware in snapshot.Hardware)
        {
            foreach (var (owner, sensor) in WithOwners(hardware))
            {
                // Pumps get their own spot in the cooling card.
                if (sensor.Type != SensorType.Fan || !sensor.HasValue
                    || sensor.Id == PumpSensorId || IsNamedPump(owner, sensor))
                    continue;

                // Super I/O chips report every fan header; hide the ones that have never spun.
                var alwaysShow = owner.Type is HardwareType.GpuNvidia or HardwareType.GpuAmd
                    or HardwareType.GpuIntel or HardwareType.Cooler;
                if (!alwaysShow && (sensor.Max ?? 0) <= 0)
                    continue;

                fans.Add((sensor.Id, _catalog.DisplayName(sensor), owner.Name, sensor.Value!.Value));
            }
        }

        var scale = Math.Max(3000f, fans.Count > 0 ? fans.Max(f => f.Rpm) : 0);
        Sync(Fans, fans.Count, () => new FanRowViewModel());
        for (var i = 0; i < fans.Count; i++)
        {
            var row = Fans[i];
            row.Name = fans[i].Name;
            row.SensorId = fans[i].Id;
            row.Source = fans[i].Source;
            row.SpeedText = fans[i].Rpm > 0 ? $"{fans[i].Rpm:N0} rpm" : "stopped";
            row.Fraction = fans[i].Rpm / scale;
        }

        FansCard.HasFans = fans.Count > 0;
        FansCard.EmptyText = _driverInstalled
            ? "No fan sensors found."
            : "Motherboard fans need the PawnIO driver.";
    }

    private void UpdateDrives(HardwareSnapshot snapshot)
    {
        var drives = snapshot.OfType(HardwareType.Storage).ToList();
        Sync(Drives, drives.Count, () => new DriveRowViewModel());
        DrivesCard.HasDrives = drives.Count > 0;
        float? hottest = null;

        for (var i = 0; i < drives.Count; i++)
        {
            var drive = drives[i];
            var row = Drives[i];
            var tempSensor = drive.AllSensors.FirstOrDefault(s => s.Type == SensorType.Temperature && s.HasValue);
            var temp = tempSensor?.Value;
            if (temp is { } dt && (hottest is null || dt > hottest)) hottest = dt;
            row.SensorId = tempSensor?.Id;
            var used = drive.Value(SensorType.Load, "Used Space");
            var health = drive.Value(SensorType.Level, "Life", "Remaining Life")
                         ?? (drive.Value(SensorType.Level, "Percentage Used") is { } wear ? 100 - wear : null);
            var read = drive.Value(SensorType.Throughput, "Read Rate");
            var write = drive.Value(SensorType.Throughput, "Write Rate");

            row.Name = drive.Name.Trim();
            row.SetVolumes(DiskNumber(drive.Id) is { } disk && snapshot.Volumes.TryGetValue(disk, out var volumes)
                ? volumes
                : []);
            row.TemperatureText = temp is { } t ? SensorFormat.Degrees(t) : SensorFormat.Empty;
            row.TemperatureBrush = temp is { } tc ? Palette.Temperature(tc) : Palette.Heat(0);
            row.UsedFraction = (used ?? 0) / 100.0;

            var details = new List<string>();
            if (used is { } u) details.Add($"{u:0}% used");
            if (health is { } h) details.Add($"{h:0}% health");
            row.Details = details.Count > 0 ? string.Join(" · ", details) : "No details";
            row.Activity = read is not null || write is not null
                ? $"R {SensorFormat.Rate(read ?? 0)}  ·  W {SensorFormat.Rate(write ?? 0)}"
                : "";
        }

        DrivesCard.Heat = Heat.Above(hottest, Heat.Limit(Rules, Heat.DriveRule), DrivesCard.Heat);
    }

    /// <summary>LHM names drives after the Windows disk number: "/nvme/2" is disk 2.</summary>
    private static int? DiskNumber(string identifier) =>
        int.TryParse(identifier[(identifier.LastIndexOf('/') + 1)..], out var n) ? n : null;

    private static readonly string[] PumpWords = ["pump", "aio"];

    private const string UsbCoolers = "Coolers with a USB controller show up here when they're supported: NZXT Kraken, "
                                      + "Aqua Computer and Corsair Commander Core, Core XT and ST.";

    private static bool NamesAPump(HardwareReading owner) =>
        owner.Sensors.Any(s => s.Type == SensorType.Fan && ContainsAny(s.Name, PumpWords));
    private static readonly string[] CoolantWords = ["water", "coolant", "liquid"];

    private void UpdateCooling(HardwareSnapshot snapshot, HardwareReading? cpu)
    {
        var pumps = new List<SensorReading>();
        var coolant = new List<SensorReading>();
        var flows = new List<SensorReading>();
        var sources = new List<string>();
        var candidates = new List<SensorReading>();
        var candidateOwners = new List<string>();
        var headers = new List<SensorReading>();
        var namedHeaders = new List<SensorReading>();
        var usbPump = false;
        SensorReading? coolerPump = null;
        var pumpOwners = new Dictionary<string, HardwareReading>();

        foreach (var hardware in snapshot.Hardware)
        {
            foreach (var (owner, sensor) in WithOwners(hardware))
            {
                // The header the user picked as the pump: always shown, even at 0 rpm, since a
                // stopped pump is exactly what they'd want to notice.
                if (sensor.Type == SensorType.Fan && sensor.Id == PumpSensorId)
                {
                    if (owner.Type is HardwareType.SuperIO or HardwareType.EmbeddedController)
                        headers.Add(sensor);
                    else if (owner.Type == HardwareType.Cooler)
                        usbPump = true;
                    pumps.Insert(0, sensor);
                    pumpOwners[sensor.Id] = owner;
                    if (!sources.Contains(owner.Name))
                        sources.Add(owner.Name);
                    continue;
                }

                // Unconnected headers (the Z790 Hero has W_IN, W_OUT and W_FLOW whether or not
                // anything is plugged in) read 0 forever, so only count sensors that have reported something.
                if (!sensor.HasValue || (sensor.Max ?? 0) <= 0)
                    continue;

                var isCooler = owner.Type == HardwareType.Cooler;
                var target = sensor.Type switch
                {
                    // A cooler that names its pump (a Corsair Commander: "Pump", then "Fan #1"...) has fans too;
                    // one that doesn't is all pump, as before.
                    SensorType.Fan when IsNamedPump(owner, sensor) || (isCooler && !NamesAPump(owner)) => pumps,
                    SensorType.Temperature when isCooler || NameMatches(sensor, CoolantWords) => coolant,
                    SensorType.Flow => flows,
                    _ => null,
                };
                if (target is null)
                {
                    // Generic motherboard headers ("Fan #2") could be the pump; let the user say so.
                    if (sensor.Type == SensorType.Fan && owner.Type is HardwareType.SuperIO or HardwareType.EmbeddedController)
                    {
                        candidates.Add(sensor);
                        candidateOwners.Add(owner.Name);
                        headers.Add(sensor);
                    }
                    continue;
                }

                target.Add(sensor);
                if (target == pumps)
                    pumpOwners[sensor.Id] = owner;
                if (target == pumps && isCooler)
                    coolerPump ??= sensor;
                // A header named as the pump is still a header: it stays in the list, to be picked or not.
                if (target == pumps && owner.Type is HardwareType.SuperIO or HardwareType.EmbeddedController)
                {
                    headers.Add(sensor);
                    namedHeaders.Add(sensor);
                }
                if (!sources.Contains(owner.Name))
                    sources.Add(owner.Name);
            }
        }

        // A USB cooler's pump is the pump, unless the user picked a motherboard header. That includes "not on a
        // header", which is exactly where a USB pump is. Alerts, the tray and the mini window then follow it.
        if (coolerPump is not null && PumpSensorId is null or "")
        {
            SetPump(coolerPump.Id);
            usbPump = true;
        }

        // One header named as the pump ("AIO Pump", or the user's rename): it is the pump, alerts included.
        if (PumpSensorId is null && namedHeaders.Count == 1)
        {
            SetPump(namedHeaders[0].Id);
            pumps.Remove(namedHeaders[0]);
            pumps.Insert(0, namedHeaders[0]);
        }

        // With only one spinning header there's nothing to choose: assume it's the pump. This happens
        // only until the user has made a choice, so their pick in the header list doesn't get overruled.
        if (pumps.Count == 0 && PumpSensorId is null && candidates.Count == 1)
        {
            SetPump(candidates[0].Id);
            pumps.Add(candidates[0]);
            sources.Add(candidateOwners[0]);
            candidates.Clear();
        }

        _coolingIds = pumps.Concat(coolant).Concat(flows).Select(s => s.Id).ToHashSet();
        // The pump reads unlike itself since Pulse started: say so, and offer every header, USB pump or not.
        var change = PumpWatch?.Change is { } c && c.SensorId == PumpSensorId ? c : null;
        UpdatePumpHeaders(headers, namedPump: pumps.Count > 0 && pumps[0].Id != PumpSensorId, usbPump && change is null);
        Cooling.SetChange(change);

        Cooling.HasData = pumps.Count + coolant.Count + flows.Count > 0;
        Cooling.Heat = pumps.Count > 0 && pumps[0].Id == PumpSensorId
            ? change is { IsSpinning: true }
                // Probably not the pump at all any more: a question, not an alarm.
                ? HeatLevel.Warm
                : Heat.Below(pumps[0].Value, Heat.Limit(Rules, Heat.PumpRule), Cooling.Heat, margin: 50)
            : HeatLevel.None;
        if (!Cooling.HasData)
        {
            if (CoolerStatuses.FirstOrDefault(c => c.State != CoolerState.Reading) is { } cooler)
            {
                Cooling.EmptyTitle = $"{cooler.Name} isn't being read";
                Cooling.EmptyText = cooler.State switch
                {
                    CoolerState.Paused => $"{cooler.Detail}. Close it (iCUE keeps a background service running too), "
                                          + "or choose \"Always read\" in Settings → USB coolers.",
                    CoolerState.Off => "Reading USB coolers is turned off in Settings → USB coolers.",
                    _ => $"{cooler.Detail}.",
                };
            }
            else if (!_isElevated)
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "Pump and coolant sensors on the motherboard need admin rights. "
                                    + "Restart Pulse as administrator to see them.";
            }
            else if (!_driverInstalled)
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "Pump speed and coolant temperatures from the motherboard's pump and water headers "
                                    + "need the PawnIO driver. " + UsbCoolers;
            }
            else if (Cooling.ShowHeaderPicker && PumpSensorId is null)
            {
                Cooling.EmptyTitle = "Which header is your pump on?";
                Cooling.EmptyText = "Your motherboard doesn't name its fan headers, so pick the one your pump is plugged into below.";
            }
            else if (PumpSensorId == "")
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "Your pump isn't on a motherboard header. " + UsbCoolers;
            }
            else
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "No spinning motherboard fan headers or water sensors were found. " + UsbCoolers;
            }
            return;
        }

        Cooling.Source = string.Join(" · ", sources);
        if (coolant.Count > 0)
        {
            Cooling.Headline = SensorFormat.Degrees(coolant[0].Value!.Value, "0.0");
            Cooling.HeadlineCaption = Label(coolant[0]);
            Cooling.SetHistory(History(coolant[0]), HistorySamples);
            Cooling.SensorId = coolant[0].Id;
        }
        else if (pumps.Count > 0)
        {
            Cooling.Headline = $"{pumps[0].Value:N0}";
            Cooling.HeadlineCaption = $"{Label(pumps[0])} · rpm";
            Cooling.SetHistory(History(pumps[0]), HistorySamples);
            Cooling.SensorId = pumps[0].Id;
        }
        else
        {
            Cooling.Headline = SensorFormat.Format(SensorType.Flow, flows[0].Value);
            Cooling.HeadlineCaption = Label(flows[0]);
            Cooling.SetHistory(History(flows[0]), HistorySamples);
            Cooling.SensorId = flows[0].Id;
        }

        var rows = new List<(string, string)>();
        // Two pumps (the header picked as the pump and a USB cooler's own pump channel) would both read "Pump".
        rows.AddRange(pumps.Select(s => (pumps.Count > 1 ? $"{Label(s)} · {Where(s, pumpOwners)}" : Label(s),
            SensorFormat.Format(s.Type, s.Value))));
        rows.AddRange(coolant.Select(s => (Label(s), SensorFormat.Format(s.Type, s.Value))));
        rows.AddRange(flows.Select(s => (Label(s), SensorFormat.Format(s.Type, s.Value))));

        var waterIn = coolant.FirstOrDefault(s => s.Name.Contains("In", StringComparison.OrdinalIgnoreCase));
        var waterOut = coolant.FirstOrDefault(s => s.Name.Contains("Out", StringComparison.OrdinalIgnoreCase));
        if (waterIn is not null && waterOut is not null)
            rows.Add(("Loop delta", SensorFormat.TemperatureDelta(waterOut.Value!.Value - waterIn.Value!.Value)));

        if (cpu?.Value(SensorType.Temperature, "CPU Package", "Core Max") is { } cpuTemp)
            rows.Add(("CPU package", SensorFormat.Format(SensorType.Temperature, cpuTemp)));

        Cooling.SetRows(rows);
    }

    /// <summary>
    /// The pump header list: every spinning motherboard header with its speed, then "not on a header".
    /// Hidden when there are no headers, or when the pump was found by name (a cooler, a "Pump" header)
    /// and the user never picked one.
    /// </summary>
    private void UpdatePumpHeaders(List<SensorReading> headers, bool namedPump, bool usbPump)
    {
        // A pump on a USB cooler needs no header.
        Cooling.ShowHeaderPicker = headers.Count > 0 && !usbPump && !(namedPump && PumpSensorId is null);
        var choices = headers
            .Select(h => (h.Id, _catalog.DisplayName(h), h.Value is { } rpm ? $"{rpm:N0} rpm" : "no reading"))
            .Append(("", NoHeaderLabel, ""))
            .ToList();
        Cooling.SyncHeaders(choices, PumpSensorId);
    }

    /// <summary>Where a pump reading comes from, in a few words: "motherboard", or the cooler's name.</summary>
    private static string Where(SensorReading sensor, Dictionary<string, HardwareReading> owners) =>
        owners.TryGetValue(sensor.Id, out var owner)
            ? owner.Type is HardwareType.SuperIO or HardwareType.EmbeddedController ? "motherboard" : owner.Name
            : "";

    /// <summary>The pump shows as "Pump" unless the user renamed it; other sensors by their display name.</summary>
    private string Label(SensorReading sensor) =>
        sensor.Id == PumpSensorId ? _catalog.DisplayName(sensor.Id, "Pump") : _catalog.DisplayName(sensor);

    /// <summary>
    /// A pump by its name ("Pump", "AIO", or the user's rename to one). A motherboard header counts only while no
    /// header is picked: after that the pick is the pump, and a header called "Pump" is only a name. It stays in the
    /// header list and the fans, instead of turning into a second pump nobody chose.
    /// </summary>
    private bool IsNamedPump(HardwareReading owner, SensorReading sensor) =>
        sensor.Type == SensorType.Fan && NameMatches(sensor, PumpWords)
        && (owner.Type is not (HardwareType.SuperIO or HardwareType.EmbeddedController) || PumpSensorId is null);

    /// <summary>Matches the original name or the user's name, so renaming a header "... pump" also counts.</summary>
    private bool NameMatches(SensorReading sensor, string[] words) =>
        ContainsAny(sensor.Name, words) || ContainsAny(_catalog.DisplayName(sensor), words);

    private static bool ContainsAny(string text, string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private static readonly string[] ChipsetWords = ["chipset", "pch"];
    private static readonly string[] BoardWords = ["motherboard", "system"];

    private void UpdateMotherboard(HardwareSnapshot snapshot)
    {
        var board = snapshot.OfType(HardwareType.Motherboard).FirstOrDefault();
        var temps = new List<SensorReading>();
        var volts = new List<SensorReading>();
        foreach (var (_, sensor) in board is null ? [] : WithOwners(board))
        {
            // Unconnected inputs read 0 (or nothing) forever.
            if (!sensor.HasValue || (sensor.Max ?? 0) <= 0)
                continue;

            // LHM's unnamed inputs ("Temperature #8") could be anything; a name the user gave says what it is.
            var renamed = _catalog.DisplayName(sensor) != sensor.Name;
            var named = renamed || !UnnamedInput.IsMatch(sensor.Name);
            if (sensor.Type == SensorType.Temperature)
            {
                // The package is on the Processor card, coolant on the cooling card.
                if (named && sensor.Name != "CPU Package" && !NameMatches(sensor, CoolantWords))
                    temps.Add(sensor);
            }
            else if (sensor.Type == SensorType.Voltage && (renamed || SupplyRail.IsMatch(sensor.Name)))
            {
                volts.Add(sensor);
            }
        }

        _boardIds = temps.Concat(volts).Select(s => s.Id).ToHashSet();
        Motherboard.Device = board?.Name ?? "Not detected";
        Motherboard.HasData = _boardIds.Count > 0;
        // No alert for these: warm from 80 °C, hot from 95 °C, both still short of where chipsets and VRMs throttle.
        Motherboard.Heat = Heat.Above(temps.Count > 0 ? temps.Max(s => s.Value) : null, 95, Motherboard.Heat, band: 15);

        if (!Motherboard.HasData)
        {
            Motherboard.EmptyText = !_isElevated
                ? "Motherboard sensors need admin rights. Restart Pulse as administrator to see them."
                : !_driverInstalled
                    ? "Chipset, VRM and board temperatures need the PawnIO driver."
                    : "No motherboard temperatures or voltages were found.";
            Motherboard.Rows.Clear();
            Motherboard.SetHistory([], HistorySamples);
            Motherboard.SensorId = null;
            return;
        }

        var headline = temps.FirstOrDefault(s => NameMatches(s, ChipsetWords))
                       ?? temps.FirstOrDefault(s => NameMatches(s, BoardWords))
                       ?? temps.FirstOrDefault()
                       ?? volts[0];
        Motherboard.Headline = headline.Type == SensorType.Temperature
            ? SensorFormat.Degrees(headline.Value!.Value)
            : SensorFormat.Format(headline.Type, headline.Value);
        Motherboard.HeadlineCaption = BoardLabel(headline);
        Motherboard.SetHistory(History(headline), HistorySamples);
        Motherboard.SensorId = headline.Id;

        var readings = temps.Concat(volts).ToList();
        Sync(Motherboard.Rows, readings.Count, () => new BoardRowViewModel());
        for (var i = 0; i < readings.Count; i++)
        {
            var sensor = readings[i];
            var row = Motherboard.Rows[i];
            row.SensorId = sensor.Id;
            row.Name = BoardLabel(sensor);
            row.ValueText = SensorFormat.Format(sensor.Type, sensor.Value);
            row.ValueBrush = sensor.Type == SensorType.Temperature ? Palette.Temperature(sensor.Value!.Value) : null;
        }
    }

    /// <summary>The sensor chip's "CPU" input is the socket, not the CPU's own sensor.</summary>
    private string BoardLabel(SensorReading sensor) =>
        _catalog.DisplayName(sensor) is var name && name == "CPU" ? "CPU socket" : name;

    private void UpdateNetwork(HardwareSnapshot snapshot)
    {
        // Follow the adapter that has moved the most data; it's almost always the one in use.
        var adapter = snapshot.OfType(HardwareType.Network)
            .MaxBy(n => (n.Value(SensorType.Data, "Data Downloaded") ?? 0) + (n.Value(SensorType.Data, "Data Uploaded") ?? 0));
        if (adapter is null) return;

        var downSensor = adapter.First(SensorType.Throughput, "Download Speed");
        var upSensor = adapter.First(SensorType.Throughput, "Upload Speed");
        var downloaded = adapter.Value(SensorType.Data, "Data Downloaded");
        var uploaded = adapter.Value(SensorType.Data, "Data Uploaded");

        Network.Adapter = adapter.Name;
        Network.DownText = SensorFormat.Rate(downSensor?.Value ?? 0);
        Network.UpText = SensorFormat.Rate(upSensor?.Value ?? 0);
        Network.Totals = $"{SensorFormat.Format(SensorType.Data, downloaded)} received · {SensorFormat.Format(SensorType.Data, uploaded)} sent";
        Network.SetHistory(History(downSensor), History(upSensor), HistorySamples);
        Network.SensorId = downSensor?.Id;
    }

    // ---- Color code ----------------------------------------------------------------------------

    private HashSet<string> _coolingIds = [];
    private HashSet<string> _boardIds = [];

    /// <summary>
    /// The accent of the card a sensor appears in, so its details chart (and a pinned tile) use the same
    /// color: the GPU card's temperature is green, not the orange of temperatures in general.
    /// </summary>
    public Color? CardAccent(string sensorId)
    {
        foreach (var card in Cards)
        {
            if (card.SensorId == sensorId) return card.Accent;
        }
        if (_coolingIds.Contains(sensorId) || sensorId == PumpSensorId) return Palette.Network;
        if (Cores.Any(c => c.SensorId == sensorId) || CpuLoad.SensorId == sensorId) return Palette.Cpu;
        if (_boardIds.Contains(sensorId)) return Palette.Board;
        if (Fans.Any(f => f.SensorId == sensorId)) return Palette.Fans;
        if (Drives.Any(d => d.SensorId == sensorId)) return Palette.Storage;
        if (Network.SensorId == sensorId) return Palette.Network;
        return null;
    }

    // ---- Pinned sensors ------------------------------------------------------------------------

    private void UpdatePinned(HardwareSnapshot? snapshot)
    {
        var pinned = _tiles.Values.Select(t => t.Card).OfType<PinnedTileViewModel>().ToList();
        if (pinned.Count == 0 || snapshot is null)
            return;

        var wanted = pinned.SelectMany(p => p.Sensors).Select(s => s.SensorId).ToHashSet();
        var found = new Dictionary<string, (SensorReading, string, HardwareType)>();
        foreach (var hardware in snapshot.Hardware)
        {
            foreach (var (owner, sensor) in WithOwners(hardware))
            {
                if (wanted.Contains(sensor.Id))
                    found[sensor.Id] = (sensor, owner.Name, owner.Type);
            }
        }

        foreach (var tile in pinned)
            tile.Update(found, _catalog, _stream, HistorySamples, CardAccent);
        // Pinned sensors with an alert limit (a coolant or drive temperature, say) warm up like the cards.
        foreach (var tile in pinned)
            tile.UpdateHeat(sensor =>
                Heat.Above(sensor.Value, AlertEngine.AboveThresholdFor(sensor.SensorId, snapshot, Rules, PumpSensorId), sensor.Heat));
        foreach (var tile in _tiles.Values.Where(t => t.IsPinned))
            tile.RefreshTitle();
    }

    public bool IsPinned(string sensorId) =>
        DashboardLayout.PinnedTileOf(DashboardLayout.Current(_settings.Current.Dashboard), sensorId) is not null;

    public IReadOnlyList<(string Id, string Title)> PinTargets() =>
        DashboardLayout.Current(_settings.Current.Dashboard)
            .Where(t => t.KindValue == TileKind.Pinned && _tiles.ContainsKey(t.Id))
            .Select(t => (t.Id, _tiles[t.Id].Title))
            .ToList();

    public void Pin(string sensorId, string? intoTileId = null) =>
        Edit(tiles => { DashboardLayout.Pin(tiles, sensorId, intoTileId); return true; });

    public void Unpin(string sensorId) => Edit(tiles => DashboardLayout.Unpin(tiles, sensorId));

    // ---- Layout and edit mode -----------------------------------------------------------------

    /// <summary>Visible tiles, in order.</summary>
    public ObservableCollection<DashboardTileViewModel> Tiles { get; } = [];

    /// <summary>Hidden cards; edit mode lists them so they can be brought back.</summary>
    public ObservableCollection<DashboardTileViewModel> HiddenTiles { get; } = [];

    [ObservableProperty] public partial bool IsEditing { get; set; }
    [ObservableProperty] public partial bool HasHiddenTiles { get; private set; }

    /// <summary>Opens the sensor search to pick a sensor to pin; set by the main view model.</summary>
    public Action? RequestPinSearch { get; set; }

    [RelayCommand]
    private void BeginEdit() => IsEditing = true;

    [RelayCommand]
    private void EndEdit() => IsEditing = false;

    [RelayCommand]
    private void HideTile(DashboardTileViewModel? tile)
    {
        if (tile is null) return;
        Edit(tiles => DashboardLayout.SetHidden(tiles, tile.Id, true));
    }

    [RelayCommand]
    private void ShowTile(DashboardTileViewModel? tile)
    {
        if (tile is null) return;
        Edit(tiles => DashboardLayout.SetHidden(tiles, tile.Id, false));
    }

    [RelayCommand]
    private void RemoveTile(DashboardTileViewModel? tile)
    {
        if (tile is null) return;
        Edit(tiles => DashboardLayout.Remove(tiles, tile.Id));
    }

    [RelayCommand]
    private void PinSensor() => RequestPinSearch?.Invoke();

    /// <summary>Back to the default arrangement of the built-in cards. Pinned tiles stay, at the end.</summary>
    [RelayCommand]
    private void ResetLayout() => ResetLayout(_settings);

    public static void ResetLayout(SettingsService settings) => settings.Update(s =>
    {
        var pinned = s.Dashboard.Tiles?.Where(t => t.KindValue == TileKind.Pinned).ToList() ?? [];
        s.Dashboard.Tiles = pinned.Count == 0 ? null : [.. DashboardLayout.Default(), .. pinned];
    });

    /// <summary>Drag and drop: puts a tile just before another one (or at the end).</summary>
    public void MoveTile(string id, string? beforeId) => Edit(tiles => DashboardLayout.Move(tiles, id, beforeId));

    public void RenameTile(string id, string? title) => Edit(tiles =>
    {
        if (tiles.Find(t => t.Id == id) is not { } tile) return false;
        var clean = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (tile.Title == clean) return false;
        tile.Title = clean;
        return true;
    });

    /// <summary>Removes one sensor from a pinned group (in edit mode).</summary>
    public void UnpinFrom(string tileId, string sensorId) => Edit(tiles => DashboardLayout.Unpin(tiles, sensorId, tileId));

    /// <summary>Applies an edit to the saved layout; <see cref="SyncTiles"/> then follows from settings.Changed.</summary>
    private void Edit(Func<List<TileSettings>, bool> edit) => _settings.Update(s =>
    {
        var tiles = DashboardLayout.Current(s.Dashboard).Select(t => t.Clone()).ToList();
        if (edit(tiles))
            s.Dashboard.Tiles = tiles;
    });

    /// <summary>Brings the tile view models in line with the saved layout, reusing what's already there.</summary>
    private void SyncTiles()
    {
        var layout = DashboardLayout.Current(_settings.Current.Dashboard);
        var ids = new HashSet<string>();
        var visible = new List<DashboardTileViewModel>();
        var hidden = new List<DashboardTileViewModel>();

        foreach (var tile in layout)
        {
            if (tile.KindValue is not { } kind || !ids.Add(tile.Id))
                continue;
            if (!_tiles.TryGetValue(tile.Id, out var vm))
            {
                vm = new DashboardTileViewModel(tile.Id, kind, kind == TileKind.Pinned ? new PinnedTileViewModel(tile.Id) : CardFor(kind),
                    (t, size) => Edit(tiles => DashboardLayout.Resize(tiles, t.Id, size)));
                _tiles[tile.Id] = vm;
            }
            vm.Size = tile.SizeValue;
            if (vm.Card is PinnedTileViewModel pinned)
            {
                pinned.Configure(tile);
                vm.RefreshTitle();
            }
            (tile.Hidden ? hidden : visible).Add(vm);
        }

        foreach (var gone in _tiles.Keys.Where(id => !ids.Contains(id)).ToList())
            _tiles.Remove(gone);

        SyncCollection(Tiles, visible);
        SyncCollection(HiddenTiles, hidden);
        HasHiddenTiles = hidden.Count > 0;
        ShowsProcesses = visible.Any(t => t.Kind == TileKind.Processes);
        UpdatePinned(_stream.Latest);
    }

    private object CardFor(TileKind kind) => kind switch
    {
        TileKind.Processor => Cpu,
        TileKind.Graphics => Gpu,
        TileKind.Memory => Memory,
        TileKind.Power => Power,
        TileKind.Cores => CoresCard,
        TileKind.Cooling => Cooling,
        TileKind.Fans => FansCard,
        TileKind.Drives => DrivesCard,
        TileKind.Network => Network,
        TileKind.Processes => ProcessesCard,
        TileKind.CpuLoad => CpuLoad,
        TileKind.Motherboard => Motherboard,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// <summary>Reorders with Move rather than rebuilding, so tiles keep their visuals (and animate into place).</summary>
    private static void SyncCollection(ObservableCollection<DashboardTileViewModel> target, List<DashboardTileViewModel> wanted)
    {
        for (var i = target.Count - 1; i >= 0; i--)
        {
            if (!wanted.Contains(target[i]))
                target.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var at = target.IndexOf(wanted[i]);
            if (at == i) continue;
            if (at < 0) target.Insert(i, wanted[i]);
            else target.Move(at, i);
        }
    }

    private static IEnumerable<(HardwareReading Owner, SensorReading Sensor)> WithOwners(HardwareReading hardware) =>
        hardware.Sensors.Select(s => (hardware, s))
            .Concat(hardware.SubHardware.SelectMany(WithOwners));

    private static void Sync<T>(ObservableCollection<T> collection, int count, Func<T> create)
    {
        while (collection.Count < count) collection.Add(create());
        while (collection.Count > count) collection.RemoveAt(collection.Count - 1);
    }
}
