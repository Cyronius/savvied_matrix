using System.Runtime.InteropServices;

namespace SavviedMatrix.Ui;

/// <summary>
/// Stops Windows blanking the display. A picture frame that the power plan switches off
/// after ten minutes is not a picture frame.
/// </summary>
public static partial class KeepAwake
{
    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private static bool _engaged;

    public static void Engage()
    {
        try
        {
            var previous = SetThreadExecutionState(
                ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired);

            if (previous == 0)
                Log.Warn("Could not suppress display sleep; the screen may blank.");
            else
                _engaged = true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Display sleep suppression unavailable: {ex.Message}");
        }
    }

    public static void Release()
    {
        if (!_engaged) return;
        try
        {
            SetThreadExecutionState(ExecutionState.Continuous);
            _engaged = false;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore the power policy: {ex.Message}");
        }
    }
}
