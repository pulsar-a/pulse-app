using LibreHardwareMonitor.Hardware;
using Pulse.Models;
using Pulse.ViewModels;
using static Pulse.Tests.Snap;

namespace Pulse.Tests;

public class SensorsViewModelTests : PulseTest
{
    private SensorsViewModel Loaded(out HardwareSnapshot snapshot)
    {
        snapshot = Fixture("non-admin.json");
        var sensors = new SensorsViewModel(NewCatalog(out _));
        sensors.Apply(snapshot);
        return sensors;
    }

    [Fact]
    public void Groups_start_collapsed()
    {
        var sensors = Loaded(out _);

        Assert.NotEmpty(sensors.Items);
        Assert.All(sensors.Items, item => Assert.IsType<HardwareGroupViewModel>(item));
    }

    [Fact]
    public void Expanding_a_group_shows_its_sensors()
    {
        var sensors = Loaded(out _);
        var gpu = sensors.Items.OfType<HardwareGroupViewModel>().Single(g => g.Name == "NVIDIA GeForce RTX 4090");

        gpu.ToggleCommand.Execute(null);

        Assert.True(gpu.IsExpanded);
        Assert.Contains(sensors.Items, i => i is SensorRowViewModel { Name: "GPU Hot Spot" });
    }

    [Fact]
    public void Search_finds_sensors_inside_collapsed_groups_and_clearing_restores_the_view()
    {
        var sensors = Loaded(out _);
        var collapsedCount = sensors.Items.Count;

        sensors.Filter = "hot spot";
        sensors.FlushFilter();

        Assert.Contains(sensors.Items, i => i is SensorRowViewModel { Name: "GPU Hot Spot" });
        Assert.DoesNotContain(sensors.Items, i => i is SensorRowViewModel { Name: "CPU Total" });

        sensors.Filter = "";
        sensors.FlushFilter();

        Assert.Equal(collapsedCount, sensors.Items.Count);
    }

    [Fact]
    public void Search_with_no_matches_says_so()
    {
        var sensors = Loaded(out _);

        sensors.Filter = "no such sensor anywhere";
        sensors.FlushFilter();

        Assert.Empty(sensors.Items);
        Assert.True(sensors.HasNoMatches);
    }

    [Fact]
    public void Rows_only_update_while_the_page_is_active()
    {
        HardwareReading Gpu(float temp) => Hardware("/gpu-nvidia/0", "GPU", HardwareType.GpuNvidia,
            Sensor("/gpu-nvidia/0/temperature/0", "GPU Core", SensorType.Temperature, temp));

        var sensors = new SensorsViewModel(NewCatalog(out _));
        sensors.Apply(Of(Gpu(40)));
        var group = sensors.Items.OfType<HardwareGroupViewModel>().Single();
        group.ToggleCommand.Execute(null);
        var row = sensors.Items.OfType<SensorRowViewModel>().Single();
        Assert.Equal("40.0 °C", row.ValueText);

        sensors.Apply(Of(Gpu(50)));
        Assert.Equal("40.0 °C", row.ValueText);

        sensors.IsActive = true;
        Assert.Equal("50.0 °C", row.ValueText);

        sensors.Apply(Of(Gpu(60)));
        Assert.Equal("60.0 °C", row.ValueText);
    }
}
