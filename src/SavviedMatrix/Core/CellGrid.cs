namespace SavviedMatrix.Core;

/// <summary>
/// One screenful of character cells. Glyph is an index into the active
/// <see cref="Render.GlyphSet"/>; Colour is an index into the active
/// <see cref="Ascii.Palette"/>.
/// </summary>
public sealed class CellGrid
{
    /// <summary>Marks a cell as an ordinary character rather than a half-block tile.</summary>
    public const byte NotHalfBlock = 255;

    public byte[] Glyph { get; }
    public byte[] Colour { get; }

    /// <summary>
    /// Colour of the lower half of the cell, or <see cref="NotHalfBlock"/> when the cell
    /// draws a character instead.
    ///
    /// Half-block cells are how the renderer gets a second row of image samples out of one
    /// character cell: the top half takes <see cref="Colour"/> and the bottom half takes
    /// this, which doubles vertical resolution and, at the fixed 1:2 cell shape, makes the
    /// effective pixels square.
    /// </summary>
    public byte[] Lower { get; }
    public int Cols { get; }
    public int Rows { get; }
    public GridPlacement Placement { get; }

    public CellGrid(int cols, int rows, GridPlacement placement)
    {
        Cols = cols;
        Rows = rows;
        Placement = placement;
        Glyph = new byte[cols * rows];
        Colour = new byte[cols * rows];
        Lower = new byte[cols * rows];
        Array.Fill(Lower, NotHalfBlock);
    }

    public int Index(int col, int row) => row * Cols + col;

    public int Length => Glyph.Length;

    /// <summary>An all-black grid of the same dimensions.</summary>
    public CellGrid BlankLike() => new(Cols, Rows, Placement);
}
