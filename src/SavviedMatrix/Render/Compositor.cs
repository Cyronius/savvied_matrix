using System.Drawing;
using SavviedMatrix.Core;

namespace SavviedMatrix.Render;

/// <summary>
/// Owns the screen-sized pixel buffer and stamps glyph blocks into it.
/// Only cells whose glyph or colour actually changed are touched, which is what makes a
/// rain frame cost a couple of thousand small copies rather than a full repaint.
/// <para>
/// The buffer is a plain managed array of 0xAARRGGBB ints and nothing here knows how it
/// reaches the screen. On a little-endian machine that layout is byte-for-byte BGRA, which
/// is what every windowing backend wants, so presenting a frame stays a straight memory
/// copy with no conversion pass.
/// </para>
/// </summary>
public sealed class Compositor
{
    private readonly int[] _pixels;
    private readonly GridGeometry _geometry;
    private readonly GlyphAtlas _atlas;

    private readonly byte[] _lastGlyph;
    private readonly byte[] _lastColour;
    private readonly byte[] _lastLower;
    private bool _forceFull = true;

    /// <summary>The screen-sized ARGB buffer, row-major and tightly packed.</summary>
    public int[] Pixels => _pixels;

    public int Width => _geometry.ScreenWidth;
    public int Height => _geometry.ScreenHeight;

    public Compositor(GridGeometry geometry, GlyphAtlas atlas)
    {
        _geometry = geometry;
        _atlas = atlas;

        _pixels = new int[geometry.ScreenWidth * geometry.ScreenHeight];
        Array.Fill(_pixels, unchecked((int)0xFF000000));

        _lastGlyph = new byte[geometry.CellCount];
        _lastColour = new byte[geometry.CellCount];
        _lastLower = new byte[geometry.CellCount];
        Array.Fill(_lastLower, Core.CellGrid.NotHalfBlock);
    }

    /// <summary>Forces the next compose to redraw every cell.</summary>
    public void Invalidate() => _forceFull = true;

    /// <summary>
    /// Stamps the given grid into the surface and returns the rectangle that changed,
    /// or <see cref="Rectangle.Empty"/> when nothing did.
    /// </summary>
    public Rectangle Compose(byte[] glyphs, byte[] colours, byte[] lowers)
    {
        int cols = _geometry.Cols;
        int rows = _geometry.Rows;
        int cellW = _geometry.CellWidth;
        int cellH = _geometry.CellHeight;
        int screenW = _geometry.ScreenWidth;

        bool full = _forceFull;
        _forceFull = false;

        int minCol = int.MaxValue, minRow = int.MaxValue;
        int maxCol = int.MinValue, maxRow = int.MinValue;

        var dest = _pixels.AsSpan();

        for (int row = 0; row < rows; row++)
        {
            int rowBase = row * cols;
            int py = _geometry.PixelY(row);

            for (int col = 0; col < cols; col++)
            {
                int i = rowBase + col;

                byte glyph = glyphs[i];
                byte colour = colours[i];
                byte lower = lowers[i];

                if (!full && _lastGlyph[i] == glyph && _lastColour[i] == colour && _lastLower[i] == lower)
                    continue;

                _lastGlyph[i] = glyph;
                _lastColour[i] = colour;
                _lastLower[i] = lower;

                var block = lower == Core.CellGrid.NotHalfBlock
                    ? _atlas.Block(glyph, colour)
                    : _atlas.HalfBlock(colour, lower);
                int px = _geometry.PixelX(col);

                for (int y = 0; y < cellH; y++)
                {
                    int destStart = (py + y) * screenW + px;
                    block.Slice(y * cellW, cellW).CopyTo(dest.Slice(destStart, cellW));
                }

                if (col < minCol) minCol = col;
                if (col > maxCol) maxCol = col;
                if (row < minRow) minRow = row;
                if (row > maxRow) maxRow = row;
            }
        }

        if (maxCol < minCol) return Rectangle.Empty;

        int x = _geometry.PixelX(minCol);
        int y0 = _geometry.PixelY(minRow);
        int w = (maxCol - minCol + 1) * cellW;
        int h = (maxRow - minRow + 1) * cellH;

        return new Rectangle(x, y0, w, h);
    }
}
