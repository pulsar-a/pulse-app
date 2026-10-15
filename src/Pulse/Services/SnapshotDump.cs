using System.IO;

namespace Pulse.Services;

/// <summary>
/// <c>Pulse.exe --dump-snapshot &lt;file&gt;</c>: reads the hardware once and writes the snapshot to JSON.
/// Used to capture real sensor layouts as test fixtures. Runs without any UI.
/// </summary>
internal static class SnapshotDump
{
    public static int Run(string path)
    {
        var computer = HardwareService.CreateComputer();
        try
        {
            computer.Open();

            // Rates and loads are computed from the difference between two reads, so read twice.
            HardwareService.TakeSnapshot(computer, new Dictionary<int, IReadOnlyList<Models.VolumeInfo>>());
            Thread.Sleep(TimeSpan.FromSeconds(1.5));
            var snapshot = HardwareService.TakeSnapshot(computer, HardwareService.QueryVolumes());

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            SnapshotJson.Save(snapshot, path);
            return ExitCodes.Ok;
        }
        catch (Exception ex)
        {
            try { File.WriteAllText(path + ".error.txt", ex.ToString()); }
            catch (IOException) { }
            return ExitCodes.SnapshotFailed;
        }
        finally
        {
            computer.Close();
        }
    }
}
