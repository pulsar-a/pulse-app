using LibreHardwareMonitor.Hardware;
using Pulse.Services;

namespace Pulse.Tests;

public class SensorFormatTests : PulseTest
{
    [Theory]
    [InlineData(SensorType.Temperature, 45.25f, "45.3 °C")]
    [InlineData(SensorType.Load, 7.04f, "7.0 %")]
    [InlineData(SensorType.Power, 102.34f, "102.3 W")]
    [InlineData(SensorType.Fan, 2537.6f, "2,538 RPM")]
    [InlineData(SensorType.Voltage, 1.2345f, "1.235 V")]
    [InlineData(SensorType.SmallData, 3402.4f, "3,402 MB")]
    public void Formats_values_with_units(SensorType type, float value, string expected) =>
        Assert.Equal(expected, SensorFormat.Format(type, value));

    [Fact]
    public void Missing_and_NaN_values_show_a_dash()
    {
        Assert.Equal(SensorFormat.Empty, SensorFormat.Format(SensorType.Temperature, null));
        Assert.Equal(SensorFormat.Empty, SensorFormat.Format(SensorType.Temperature, float.NaN));
    }

    [Theory]
    [InlineData(5680f, "5.68 GHz")]
    [InlineData(999f, "999 MHz")]
    [InlineData(1000f, "1.00 GHz")]
    public void Clocks_switch_to_GHz_at_1000_MHz(float mhz, string expected) =>
        Assert.Equal(expected, SensorFormat.Clock(mhz));

    [Theory]
    [InlineData(500f, "500 B/s")]
    [InlineData(1536f, "1.5 KB/s")]
    [InlineData(22999040f, "21.9 MB/s")]
    public void Rates_use_binary_units(float bytesPerSecond, string expected) =>
        Assert.Equal(expected, SensorFormat.Rate(bytesPerSecond));

    [Theory]
    [InlineData(59f, "0m 59s")]
    [InlineData(3700f, "1h 1m")]
    [InlineData(20606f * 10, "2d 9h")]
    public void Durations_show_the_two_largest_units(float seconds, string expected) =>
        Assert.Equal(expected, SensorFormat.Duration(seconds));
}
