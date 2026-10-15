namespace Pulse;

/// <summary>Process exit codes, for scripts like <c>make snapshot</c>.</summary>
public static class ExitCodes
{
    public const int Ok = 0;
    public const int AlreadyRunning = 2;
    public const int SnapshotFailed = 3;
}
