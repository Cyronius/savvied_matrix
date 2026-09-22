using SavviedMatrix.Ascii;
using SavviedMatrix.Core;

namespace SavviedMatrix.Render;

/// <summary>
/// Writes plain text into a character grid. Used only for status messages, so the kiosk
/// can say what is wrong on the screen instead of silently showing black.
/// </summary>
public static class GridText
{
    /// <summary>Writes <paramref name="text"/> centred on the given row, clipped to the grid.</summary>
    public static void WriteCentred(CellGrid grid, GlyphSet glyphs, string text, int row, byte colour)
    {
        if (row < 0 || row >= grid.Rows) return;

        int start = Math.Max(0, (grid.Cols - text.Length) / 2);

        for (int i = 0; i < text.Length; i++)
        {
            int col = start + i;
            if (col >= grid.Cols) break;

            int index = grid.Index(col, row);
            grid.Glyph[index] = glyphs.IndexOf(text[i]);
            grid.Colour[index] = colour;
        }
    }

    /// <summary>A black grid carrying centred lines of dim text.</summary>
    public static CellGrid Message(
        GridGeometry geometry, GlyphSet glyphs, Palette palette, params string[] lines)
    {
        var grid = new CellGrid(geometry.Cols, geometry.Rows, new GridPlacement(0, 0, 0, 0));

        // Mid brightness in whichever palette is active, so the text is legible without
        // shouting.
        byte colour = palette.ShadeIndex(0.55f);

        int first = Math.Max(0, geometry.Rows / 2 - lines.Length / 2);
        for (int i = 0; i < lines.Length; i++)
            WriteCentred(grid, glyphs, lines[i], first + i, colour);

        return grid;
    }
}
