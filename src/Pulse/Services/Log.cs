using System.IO;

namespace Pulse.Services;

/// <summary>
/// A plain-text diagnostic log at %LOCALAPPDATA%\Pulse\pulse.log, recreated on every start.
/// Useful when hardware shows up differently than expected.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();

    /// <summary>Tests point this at a temporary file so they don't touch the real log.</summary>
    internal static string FilePath { get; set; } = Path.Combine(SettingsService.DefaultFolder, "pulse.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, "");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        Write($"Pulse started. Admin: {HardwareService.IsElevated}, PawnIO: {HardwareService.IsDriverInstalled}");
    }

    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(FilePath, $"{DateTime.Now:HH:mm:ss.fff}  {message}{Environment.NewLine}");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
