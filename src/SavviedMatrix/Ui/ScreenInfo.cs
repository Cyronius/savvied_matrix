using Avalonia.Controls;

namespace SavviedMatrix.Ui;

/// <summary>
/// The size of the display the kiosk will fill, in physical pixels.
/// <para>
/// The grid is laid out in real pixels so a cell is a fixed number of them, rather than in
/// the scaled units the window system lays out in. On a television at 100 per cent the two
/// are the same; on a scaled desktop they are not, and using the scaled size would make the
/// characters land between pixels and blur.
/// </para>
/// </summary>
public static class ScreenInfo
{
    public const int FallbackWidth = 1920;
    public const int FallbackHeight = 1080;

    /// <summary>
    /// Primary screen size in physical pixels, with its scale factor. Falls back to 1080p
    /// if the platform will not say, which is the television this is built for.
    /// </summary>
    public static (int Width, int Height, double Scaling) Primary()
    {
        try
        {
            // Screens are only reachable through a top-level window. This one is never
            // shown: constructing it is enough to attach it to the platform.
            var probe = new Window();

            try
            {
                var screens = probe.Screens;
                var screen = screens?.Primary ?? screens?.All.FirstOrDefault();

                if (screen is not null)
                {
                    var bounds = screen.Bounds;
                    double scaling = screen.Scaling > 0 ? screen.Scaling : 1.0;

                    if (bounds.Width > 0 && bounds.Height > 0)
                        return (bounds.Width, bounds.Height, scaling);
                }
            }
            finally
            {
                probe.Close();
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not read the screen size ({ex.Message}); assuming {FallbackWidth}x{FallbackHeight}.");
        }

        return (FallbackWidth, FallbackHeight, 1.0);
    }
}
