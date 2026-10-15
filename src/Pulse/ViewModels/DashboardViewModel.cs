using System.Collections.ObjectModel;
using System.Text.RegularExpressions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LibreHardwareMonitor.Hardware;
using Pulse.Controls;
using Pulse.Models;
using Pulse.Services;
using Wpf.Ui.Controls;

namespace Pulse.ViewModels;

public partial class DashboardViewModel : ObservableObject
{
    private static readonly Regex CoreTemperature = new(@"^(P-Core|E-Core|Core|CPU Core) #(\d+)$");
    private static readonly Regex CoreLoad = new(@"^CPU Core #(\d+)( Thread #\d+)?$");
    private static readonly Regex PsuCapacity = new(@"(\d{3,4})");
    private static readonly Regex DimmTemperature = new(@"^DIMM #\d+$");
    private static readonly Regex DimmSlot = new(@"\s*\(#\d+\)$");

    private readonly bool _driverInstalled;
    private readonly bool _isElevated = HardwareService.IsElevated;
    private readonly SettingsService _settings;

    public DashboardViewModel(bool driverInstalled, SettingsService settings)
    {
        _driverInstalled = driverInstalled;
        _settings = settings;
        Cards = [Cpu, Gpu, Memory, Power];
    }

    private string? PumpSensorId => _settings.Current.PumpSensorId;

    /// <summary>Remembers which fan header the pump is on; the next tick moves it into the cooling card.</summary>
    [RelayCommand]
    private void SetPump(string sensorId)
    {
        _settings.Current.PumpSensorId = sensorId;
        _settings.Save();
        Log.Write($"Pump set to {sensorId}");
    }

    /// <summary>
    /// Shows the header picker again. Stores an empty id rather than null, meaning "the user chose",
    /// so a lone header isn't automatically re-picked straight away.
    /// </summary>
    [RelayCommand]
    private void ClearPump()
    {
        _settings.Current.PumpSensorId = "";
        _settings.Save();
        Log.Write("Pump selection cleared");
    }

    public GaugeCardViewModel Cpu { get; } = new("Processor", SymbolRegular.DeveloperBoard24, Palette.Cpu);
    public GaugeCardViewModel Gpu { get; } = new("Graphics", SymbolRegular.DesktopPulse24, Palette.Gpu);
    public GaugeCardViewModel Memory { get; } = new("Memory", SymbolRegular.Ram20, Palette.Memory);
    public GaugeCardViewModel Power { get; } = new("System power", SymbolRegular.Flash24, Palette.Power);
    public IReadOnlyList<GaugeCardViewModel> Cards { get; }

    public ObservableCollection<CoreTileViewModel> Cores { get; } = [];
    public ObservableCollection<FanRowViewModel> Fans { get; } = [];
    public ObservableCollection<DriveRowViewModel> Drives { get; } = [];
    public NetworkViewModel Network { get; } = new();
    public CoolingViewModel Cooling { get; } = new();

    /// <summary>True until the first snapshot arrives; the view shows skeleton cards meanwhile.</summary>
    [ObservableProperty] public partial bool IsLoading { get; set; } = true;
    [ObservableProperty] public partial string CoresTitle { get; set; } = "Cores";
    [ObservableProperty] public partial string CoresSubtitle { get; set; } = "";
    [ObservableProperty] public partial bool HasFans { get; set; }
    [ObservableProperty] public partial bool HasDrives { get; set; }
    [ObservableProperty] public partial string FansEmptyText { get; set; } = "";

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
        UpdateCooling(snapshot, cpu);
        UpdateFans(snapshot);
        UpdateDrives(snapshot);
        UpdateNetwork(snapshot);
        IsLoading = false;
    }

    private void UpdateCpu(HardwareReading? cpu)
    {
        if (cpu is null) return;

        var temp = cpu.Value(SensorType.Temperature, "CPU Package", "Core Max", "Core Average", "Core (Tctl/Tdie)");
        var load = cpu.Value(SensorType.Load, "CPU Total");
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
            Cpu.SetGauge("temperature", t, 100, v => $"{v:0}°");
            Cpu.GaugeCaption = "package";
            Cpu.HistoryMax = 100;
            Cpu.HistoryLabel = "Temperature, last minute";
            Cpu.PushHistory(t);
        }
        else
        {
            Cpu.SetGauge("load", load, 100, v => $"{v:0}%");
            Cpu.GaugeCaption = "load";
            Cpu.HistoryMax = 100;
            Cpu.HistoryLabel = "Load, last minute";
            Cpu.PushHistory(load);
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

        var temp = gpu.Value(SensorType.Temperature, "GPU Core");
        var hotSpot = gpu.Value(SensorType.Temperature, "GPU Hot Spot");
        var memJunction = gpu.Value(SensorType.Temperature, "GPU Memory Junction");
        var load = gpu.Value(SensorType.Load, "GPU Core");
        var power = gpu.Value(SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        var clock = gpu.Value(SensorType.Clock, "GPU Core");
        var vramUsed = gpu.Value(SensorType.SmallData, "GPU Memory Used", "D3D Dedicated Memory Used");
        var vramTotal = gpu.Value(SensorType.SmallData, "GPU Memory Total");

        Gpu.Device = gpu.Name;
        Gpu.SetGauge("temperature", temp, 100, v => $"{v:0}°");
        Gpu.GaugeCaption = hotSpot is { } h ? $"hot spot {h:0}°" : "core";
        Gpu.HistoryMax = 100;
        Gpu.HistoryLabel = "Load, last minute";
        Gpu.PushHistory(load);

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

        var load = ram.Value(SensorType.Load, "Memory");
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
        Memory.PushHistory(load);

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
        var gpuPower = gpu?.Value(SensorType.Power, "GPU Package", "GPU Power", "GPU Core");
        var psu = snapshot.OfType(HardwareType.Psu).FirstOrDefault();

        if (psu is not null)
        {
            var total = psu.Value(SensorType.Power, "Total watts", "Total Output");
            var output = psu.Value(SensorType.Power, "Total Output");
            var capacity = PsuCapacity.Match(psu.Name) is { Success: true } m ? float.Parse(m.Value) : 1000f;
            var psuTemp = psu.AllSensors.Where(s => s.Type == SensorType.Temperature && s.HasValue)
                .Select(s => s.Value).Max();

            Power.Device = psu.Name;
            Power.SetGauge("psu", total, capacity, v => $"{v:0} W");
            Power.GaugeCaption = $"of {capacity:0} W";
            Power.HistoryMax = double.NaN;
            Power.HistoryLabel = "System draw, last minute";
            Power.PushHistory(total);
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
        Power.GaugeCaption = "CPU + GPU";
        Power.HistoryMax = double.NaN;
        Power.HistoryLabel = "Draw, last minute";
        Power.PushHistory(sum);
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

        List<(string Label, string Name, float? Value)> tiles;
        bool showingTemps;

        if (temps.Any(s => s.HasValue))
        {
            showingTemps = true;
            tiles = temps.Select(s => (ShortCoreLabel(s.Name), s.Name, s.Value)).ToList();
            var pCores = temps.Count(s => s.Name.StartsWith("P-Core"));
            var eCores = temps.Count(s => s.Name.StartsWith("E-Core"));
            var hottest = temps.Where(s => s.HasValue).MaxBy(s => s.Value)!;
            CoresTitle = "Core temperatures";
            CoresSubtitle = (pCores > 0 && eCores > 0 ? $"{pCores} P-cores, {eCores} E-cores · " : $"{temps.Count} cores · ")
                            + $"hottest {hottest.Name} at {hottest.Value:0} °C";
        }
        else
        {
            // Without the driver there are no temperatures, but Windows still reports per-core load.
            showingTemps = false;
            tiles = cpu.Sensors
                .Where(s => s.Type == SensorType.Load && CoreLoad.IsMatch(s.Name))
                .GroupBy(s => int.Parse(CoreLoad.Match(s.Name).Groups[1].Value))
                .OrderBy(g => g.Key)
                .Select(g => ($"{g.Key}", $"Core #{g.Key}", g.Max(s => s.Value)))
                .ToList();
            CoresTitle = "Core load";
            CoresSubtitle = _driverInstalled
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
            var (label, name, value) = tiles[i];
            var tile = Cores[i];
            tile.Label = label;
            if (value is not { } v || float.IsNaN(v))
            {
                tile.ValueText = "";
                tile.Fraction = 0;
                tile.ToolTip = $"{name}: no reading";
                continue;
            }

            if (showingTemps)
            {
                tile.ValueText = $"{v:0}";
                tile.Fraction = Math.Clamp((v - 20) / 80.0, 0.04, 1);
                tile.Fill = Palette.Temperature(v);
                tile.ToolTip = $"{name}: {v:0.0} °C";
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
                    || sensor.Id == PumpSensorId || ContainsAny(sensor.Name, PumpWords))
                    continue;

                // Super I/O chips report every fan header; hide the ones that have never spun.
                var alwaysShow = owner.Type is HardwareType.GpuNvidia or HardwareType.GpuAmd
                    or HardwareType.GpuIntel or HardwareType.Cooler;
                if (!alwaysShow && (sensor.Max ?? 0) <= 0)
                    continue;

                fans.Add((sensor.Id, sensor.Name, owner.Name, sensor.Value!.Value));
            }
        }

        var scale = Math.Max(3000f, fans.Count > 0 ? fans.Max(f => f.Rpm) : 0);
        Sync(Fans, fans.Count, () => new FanRowViewModel());
        for (var i = 0; i < fans.Count; i++)
        {
            var row = Fans[i];
            row.Name = fans[i].Name;
            row.Source = fans[i].Source;
            row.SpeedText = fans[i].Rpm > 0 ? $"{fans[i].Rpm:N0} rpm" : "stopped";
            row.Fraction = fans[i].Rpm / scale;
        }

        HasFans = fans.Count > 0;
        FansEmptyText = _driverInstalled
            ? "No fan sensors found."
            : "Motherboard fans need the PawnIO driver.";
    }

    private void UpdateDrives(HardwareSnapshot snapshot)
    {
        var drives = snapshot.OfType(HardwareType.Storage).ToList();
        Sync(Drives, drives.Count, () => new DriveRowViewModel());
        HasDrives = drives.Count > 0;

        for (var i = 0; i < drives.Count; i++)
        {
            var drive = drives[i];
            var row = Drives[i];
            var temp = drive.AllSensors.FirstOrDefault(s => s.Type == SensorType.Temperature && s.HasValue)?.Value;
            var used = drive.Value(SensorType.Load, "Used Space");
            var health = drive.Value(SensorType.Level, "Life", "Remaining Life")
                         ?? (drive.Value(SensorType.Level, "Percentage Used") is { } wear ? 100 - wear : null);
            var read = drive.Value(SensorType.Throughput, "Read Rate");
            var write = drive.Value(SensorType.Throughput, "Write Rate");

            row.Name = drive.Name.Trim();
            row.SetVolumes(DiskNumber(drive.Id) is { } disk && snapshot.Volumes.TryGetValue(disk, out var volumes)
                ? volumes
                : []);
            row.TemperatureText = temp is { } t ? $"{t:0}°" : SensorFormat.Empty;
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
    }

    /// <summary>LHM names drives after the Windows disk number: "/nvme/2" is disk 2.</summary>
    private static int? DiskNumber(string identifier) =>
        int.TryParse(identifier[(identifier.LastIndexOf('/') + 1)..], out var n) ? n : null;

    private static readonly string[] PumpWords = ["pump", "aio"];
    private static readonly string[] CoolantWords = ["water", "coolant", "liquid"];

    private void UpdateCooling(HardwareSnapshot snapshot, HardwareReading? cpu)
    {
        var pumps = new List<SensorReading>();
        var coolant = new List<SensorReading>();
        var flows = new List<SensorReading>();
        var sources = new List<string>();
        var candidates = new List<SensorReading>();
        var candidateOwners = new List<string>();

        foreach (var hardware in snapshot.Hardware)
        {
            foreach (var (owner, sensor) in WithOwners(hardware))
            {
                // The header the user picked as the pump: always shown, even at 0 rpm, since a
                // stopped pump is exactly what they'd want to notice.
                if (sensor.Type == SensorType.Fan && sensor.Id == PumpSensorId)
                {
                    pumps.Insert(0, sensor with { Name = "Pump" });
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
                    SensorType.Fan when isCooler || ContainsAny(sensor.Name, PumpWords) => pumps,
                    SensorType.Temperature when isCooler || ContainsAny(sensor.Name, CoolantWords) => coolant,
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
                    }
                    continue;
                }

                target.Add(sensor);
                if (!sources.Contains(owner.Name))
                    sources.Add(owner.Name);
            }
        }

        // With only one spinning header there's nothing to choose: assume it's the pump. This happens
        // only until the user has made a choice, so "Change pump header" doesn't get overruled.
        if (pumps.Count == 0 && PumpSensorId is null && candidates.Count == 1)
        {
            SetPump(candidates[0].Id);
            pumps.Add(candidates[0] with { Name = "Pump" });
            sources.Add(candidateOwners[0]);
            candidates.Clear();
        }

        UpdatePumpCandidates(pumps.Count == 0 ? candidates : []);
        Cooling.CanChangePump = pumps.Count > 0 && pumps[0].Id == PumpSensorId;

        Cooling.HasData = pumps.Count + coolant.Count + flows.Count > 0;
        if (!Cooling.HasData)
        {
            if (!_isElevated)
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "Pump and coolant sensors on the motherboard need admin rights. "
                                    + "Restart Pulse as administrator to see them.";
            }
            else if (!_driverInstalled)
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "Pump speed and coolant temperatures from the motherboard's pump and water headers "
                                    + "need the PawnIO driver. Corsair coolers connected over USB aren't supported "
                                    + "by the sensor library yet.";
            }
            else if (Cooling.ShowPumpPicker)
            {
                Cooling.EmptyTitle = "Which header is your pump on?";
                Cooling.EmptyText = "Your motherboard doesn't name its fan headers, so pick the one your pump is plugged into.";
            }
            else
            {
                Cooling.EmptyTitle = "No pump or coolant readings";
                Cooling.EmptyText = "No spinning motherboard fan headers or water sensors were found. Corsair coolers "
                                    + "connected over USB aren't supported by the sensor library yet.";
            }
            return;
        }

        Cooling.Source = string.Join(" · ", sources);
        if (coolant.Count > 0)
        {
            Cooling.Headline = $"{coolant[0].Value:0.0}°";
            Cooling.HeadlineCaption = coolant[0].Name.ToLowerInvariant();
            Cooling.PushHistory(coolant[0].Value!.Value);
        }
        else if (pumps.Count > 0)
        {
            Cooling.Headline = $"{pumps[0].Value:N0}";
            Cooling.HeadlineCaption = $"{pumps[0].Name.ToLowerInvariant()}, rpm";
            Cooling.PushHistory(pumps[0].Value!.Value);
        }
        else
        {
            Cooling.Headline = SensorFormat.Format(SensorType.Flow, flows[0].Value);
            Cooling.HeadlineCaption = flows[0].Name.ToLowerInvariant();
            Cooling.PushHistory(flows[0].Value!.Value);
        }

        var rows = new List<(string, string)>();
        rows.AddRange(pumps.Select(s => (s.Name, SensorFormat.Format(s.Type, s.Value))));
        rows.AddRange(coolant.Select(s => (s.Name, SensorFormat.Format(s.Type, s.Value))));
        rows.AddRange(flows.Select(s => (s.Name, SensorFormat.Format(s.Type, s.Value))));

        var waterIn = coolant.FirstOrDefault(s => s.Name.Contains("In", StringComparison.OrdinalIgnoreCase));
        var waterOut = coolant.FirstOrDefault(s => s.Name.Contains("Out", StringComparison.OrdinalIgnoreCase));
        if (waterIn is not null && waterOut is not null)
            rows.Add(("Loop delta", $"{waterOut.Value - waterIn.Value:0.0} °C"));

        if (cpu?.Value(SensorType.Temperature, "CPU Package", "Core Max") is { } cpuTemp)
            rows.Add(("CPU package", SensorFormat.Format(SensorType.Temperature, cpuTemp)));

        Cooling.SetRows(rows);
    }

    private void UpdatePumpCandidates(List<SensorReading> candidates)
    {
        if (!Cooling.Candidates.Select(c => c.Id).SequenceEqual(candidates.Select(c => c.Id)))
        {
            Cooling.Candidates.Clear();
            foreach (var sensor in candidates)
                Cooling.Candidates.Add(new PumpCandidateViewModel { Id = sensor.Id });
        }

        for (var i = 0; i < candidates.Count; i++)
        {
            Cooling.Candidates[i].Name = candidates[i].Name;
            Cooling.Candidates[i].SpeedText = $"{candidates[i].Value:N0} rpm";
        }
        Cooling.ShowPumpPicker = candidates.Count > 0;
    }

    private static bool ContainsAny(string text, string[] words) =>
        words.Any(w => text.Contains(w, StringComparison.OrdinalIgnoreCase));

    private string? _adapterId;

    private void UpdateNetwork(HardwareSnapshot snapshot)
    {
        // Follow the adapter that has moved the most data; it's almost always the one in use.
        var adapter = snapshot.OfType(HardwareType.Network)
            .MaxBy(n => (n.Value(SensorType.Data, "Data Downloaded") ?? 0) + (n.Value(SensorType.Data, "Data Uploaded") ?? 0));
        if (adapter is null) return;

        if (adapter.Id != _adapterId)
        {
            _adapterId = adapter.Id;
            Network.Reset();
        }

        var down = adapter.Value(SensorType.Throughput, "Download Speed") ?? 0;
        var up = adapter.Value(SensorType.Throughput, "Upload Speed") ?? 0;
        var downloaded = adapter.Value(SensorType.Data, "Data Downloaded");
        var uploaded = adapter.Value(SensorType.Data, "Data Uploaded");

        Network.Adapter = adapter.Name;
        Network.DownText = SensorFormat.Rate(down);
        Network.UpText = SensorFormat.Rate(up);
        Network.Totals = $"{SensorFormat.Format(SensorType.Data, downloaded)} received · {SensorFormat.Format(SensorType.Data, uploaded)} sent";
        Network.Push(down, up);
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
