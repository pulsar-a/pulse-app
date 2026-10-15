using System.IO;
using Pulse.Services;

namespace Pulse.Tests;

public class SnapshotJsonTests : PulseTest
{
    [Fact]
    public void Fixture_survives_a_save_and_load_round_trip()
    {
        var original = Snap.Fixture("non-admin.json");
        var path = Path.Combine(TempFolder, "copy.json");

        SnapshotJson.Save(original, path);
        var copy = SnapshotJson.Load(path);

        Assert.Equal(
            original.Hardware.SelectMany(h => h.AllSensors).Select(s => (s.Id, s.Value)),
            copy.Hardware.SelectMany(h => h.AllSensors).Select(s => (s.Id, s.Value)));
        Assert.Equal(original.Volumes.Keys.Order(), copy.Volumes.Keys.Order());
    }
}
