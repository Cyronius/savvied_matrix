using SavviedMatrix.Audio;
using SavviedMatrix.Core;

namespace SavviedMatrix.Ascii;

/// <summary>
/// Everything the display loop needs about one image: what to draw, and what it sounds like.
/// Both are derived in the same background pass so the UI thread never analyses anything.
/// </summary>
public sealed class Frame
{
    public CellGrid Cells { get; }
    public BandStats[] Bands { get; }
    public string Name { get; }

    public Frame(CellGrid cells, BandStats[] bands, string name)
    {
        Cells = cells;
        Bands = bands;
        Name = name;
    }

    /// <summary>A black frame carrying no audio, used before the first image and on failure.</summary>
    public static Frame Blank(int cols, int rows, string name = "")
        => new(new CellGrid(cols, rows, new GridPlacement(0, 0, 0, 0)), Array.Empty<BandStats>(), name);
}
