using System.IO;

namespace Pulse.Services;

/// <summary>
/// A plain-text diagnostic log at %LOCALAPPDATA%\Pulse\pulse.log, recreated on every start.
/// Useful when hardware shows up differently than expected.
/// </summary>
public static class Log
{
    private static readonly object Gate = new();
    private static readonly string FilePath = Path.Combine(SettingsService.Folder, "pulse.log");

    public static void Start()
    {
        try
        {
            Directory.CreateDirectory(SettingsService.Folder);
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
