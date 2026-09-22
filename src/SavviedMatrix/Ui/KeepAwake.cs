using System.Runtime.InteropServices;

namespace SavviedMatrix.Ui;

/// <summary>
/// Stops the machine blanking the display. A picture frame that the power settings switch
/// off after ten minutes is not a picture frame.
/// <para>
/// There is no portable way to say this, so each platform gets its own call: the execution
/// state on Windows and a power assertion on macOS. Both are held for the life of the
/// process and released on exit; both are advisory, and failing to get one is logged and
/// otherwise ignored, because a screen that blanks is better than a kiosk that will not run.
/// </para>
/// <para>
/// This has to run before the UI toolkit starts, not after. macOS drops a sleeping display
/// out of the active list, and the renderer's frame clock is built from that list, so an app
/// that launches while the screen is already dark fails to start at all. Waking the display
/// first and waiting for it to come back turns the most common kiosk startup - a machine
/// that has been sitting idle since it booted - into an ordinary one.
/// </para>
/// </summary>
public static partial class KeepAwake
{
    private static bool _engaged;

    /// <summary>
    /// How long to wait for a woken display to rejoin the active list. Waking is not
    /// instant and the renderer cannot start until it has happened; a second or so is
    /// typical, and giving up after this is better than hanging forever on a machine
    /// that genuinely has no screen attached.
    /// </summary>
    private static readonly TimeSpan DisplayWakeTimeout = TimeSpan.FromSeconds(5);

    public static void Engage()
    {
        if (_engaged) return;

        try
        {
            if (OperatingSystem.IsWindows()) _engaged = EngageWindows();
            else if (OperatingSystem.IsMacOS()) _engaged = EngageMac();
            else Log.Warn("Display sleep suppression is not implemented on this platform; the screen may blank.");
        }
        catch (Exception ex)
        {
            Log.Warn($"Display sleep suppression unavailable: {ex.Message}");
        }
    }

    public static void Release()
    {
        if (!_engaged) return;
        _engaged = false;

        try
        {
            if (OperatingSystem.IsWindows()) ReleaseWindows();
            else if (OperatingSystem.IsMacOS()) ReleaseMac();
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not restore the power policy: {ex.Message}");
        }
    }

    // ---------------------------------------------------------------- Windows

    [Flags]
    private enum ExecutionState : uint
    {
        SystemRequired = 0x00000001,
        DisplayRequired = 0x00000002,
        Continuous = 0x80000000
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial ExecutionState SetThreadExecutionState(ExecutionState esFlags);

    private static bool EngageWindows()
    {
        var previous = SetThreadExecutionState(
            ExecutionState.Continuous | ExecutionState.DisplayRequired | ExecutionState.SystemRequired);

        if (previous != 0) return true;

        Log.Warn("Could not suppress display sleep; the screen may blank.");
        return false;
    }

    private static void ReleaseWindows() => SetThreadExecutionState(ExecutionState.Continuous);

    // ---------------------------------------------------------------- macOS

    private const string IOKit = "/System/Library/Frameworks/IOKit.framework/IOKit";
    private const string CoreFoundation =
        "/System/Library/Frameworks/CoreFoundation.framework/CoreFoundation";

    /// <summary>kIOPMAssertionLevelOn.</summary>
    private const uint AssertionLevelOn = 255;

    /// <summary>kCFStringEncodingUTF8.</summary>
    private const uint Utf8Encoding = 0x08000100;

    /// <summary>
    /// kIOPMAssertionTypeNoDisplaySleep. Keeps the panel lit, which also keeps the system
    /// awake; the idle-system assertion alone would let the screen go dark.
    /// </summary>
    private const string NoDisplaySleep = "NoDisplaySleepAssertion";

    [LibraryImport(CoreFoundation, StringMarshalling = StringMarshalling.Utf8)]
    private static partial IntPtr CFStringCreateWithCString(IntPtr alloc, string cStr, uint encoding);

    [LibraryImport(CoreFoundation)]
    private static partial void CFRelease(IntPtr cf);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionCreateWithName(
        IntPtr assertionType, uint assertionLevel, IntPtr assertionName, out uint assertionId);

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionRelease(uint assertionId);

    /// <summary>kIOPMUserActiveLocal: someone is at this machine, not on a remote session.</summary>
    private const uint UserActiveLocal = 0;

    [LibraryImport(IOKit)]
    private static partial int IOPMAssertionDeclareUserActivity(
        IntPtr assertionName, uint userType, out uint assertionId);

    private const string CoreGraphics =
        "/System/Library/Frameworks/CoreGraphics.framework/CoreGraphics";

    /// <summary>
    /// Passing a null list asks only for the count, which is all this needs: a sleeping
    /// display is not on the list at all, so a non-zero count means the screen is back.
    /// </summary>
    [LibraryImport(CoreGraphics)]
    private static partial int CGGetActiveDisplayList(
        uint maxDisplays, uint[]? activeDisplays, out uint displayCount);

    private static uint _assertionId;

    private static bool EngageMac()
    {
        // Wake the panel before anything asks the window server about it. An assertion
        // only prevents the next sleep; it does not undo one that has already happened.
        DeclareUserActivity();

        IntPtr type = IntPtr.Zero, name = IntPtr.Zero;

        try
        {
            type = CFStringCreateWithCString(IntPtr.Zero, NoDisplaySleep, Utf8Encoding);
            name = CFStringCreateWithCString(IntPtr.Zero, "SavviedMatrix is displaying images", Utf8Encoding);

            if (type == IntPtr.Zero || name == IntPtr.Zero)
            {
                Log.Warn("Could not build the power assertion strings; the screen may blank.");
                return false;
            }

            // kIOReturnSuccess is 0.
            int result = IOPMAssertionCreateWithName(type, AssertionLevelOn, name, out uint id);

            if (result != 0)
            {
                Log.Warn($"Could not suppress display sleep (IOKit returned 0x{result:X8}); the screen may blank.");
                return false;
            }

            _assertionId = id;
            WaitForActiveDisplay();
            return true;
        }
        finally
        {
            if (type != IntPtr.Zero) CFRelease(type);
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    /// <summary>
    /// Tells the system the user is present, which wakes a sleeping display. Separate from
    /// the assertion above: that one keeps the screen from going dark later, this one brings
    /// it back now.
    /// </summary>
    private static void DeclareUserActivity()
    {
        IntPtr name = IntPtr.Zero;

        try
        {
            name = CFStringCreateWithCString(IntPtr.Zero, "SavviedMatrix is starting", Utf8Encoding);
            if (name == IntPtr.Zero) return;

            int result = IOPMAssertionDeclareUserActivity(name, UserActiveLocal, out _);

            if (result != 0)
                Log.Warn($"Could not wake the display (IOKit returned 0x{result:X8}).");
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not wake the display: {ex.Message}");
        }
        finally
        {
            if (name != IntPtr.Zero) CFRelease(name);
        }
    }

    /// <summary>
    /// Blocks until the display is listed as active, or until the timeout. A display that
    /// has just been woken takes a moment to come back, and the renderer's frame clock is
    /// built from the active list, so starting the UI before then is what fails.
    /// </summary>
    private static void WaitForActiveDisplay()
    {
        var deadline = DateTime.UtcNow + DisplayWakeTimeout;
        bool waited = false;

        while (true)
        {
            try
            {
                if (CGGetActiveDisplayList(0, null, out uint count) == 0 && count > 0)
                {
                    if (waited) Log.Info("Display is awake.");
                    return;
                }
            }
            catch (Exception ex)
            {
                // Without this call there is nothing to wait for; let the UI try anyway.
                Log.Warn($"Could not read the display list: {ex.Message}");
                return;
            }

            if (DateTime.UtcNow >= deadline)
            {
                Log.Warn(
                    "No active display after waiting for one to wake; starting anyway, "
                    + "which may fail if nothing is connected.");
                return;
            }

            waited = true;
            Thread.Sleep(100);
        }
    }

    private static void ReleaseMac()
    {
        if (_assertionId == 0) return;
        IOPMAssertionRelease(_assertionId);
        _assertionId = 0;
    }
}
