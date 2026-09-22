using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using SavviedMatrix.Ascii;
using SavviedMatrix.Core;

namespace SavviedMatrix.Render;

/// <summary>
/// Every glyph, pre-rendered once per palette colour into a flat pixel block.
/// Drawing a frame then costs a memory copy per changed cell instead of a text draw,
/// which is the difference between the rain animation being possible on a weak PC and not.
/// </summary>
public sealed class GlyphAtlas : IDisposable
{
    private readonly int[] _pixels;
    private readonly int[] _halfBlocks;
    private readonly int _blockSize;
    private readonly int _colourCount;

    public int CellWidth { get; }
    public int CellHeight { get; }
    public int GlyphCount { get; }

    /// <summary>
    /// Mean ink coverage of each glyph in [0,1], measured from the rendered mask rather
    /// than assumed from the character. How dark a character actually looks depends on the
    /// font and the cell size, not on where it sits in a conventional ramp.
    /// </summary>
    public float[] Coverage { get; }

    /// <summary>
    /// Vertical centre of mass of each glyph's ink in [0,1]. Used to prefer glyphs whose
    /// ink sits in the middle of the cell: an underscore and a quotation mark have almost
    /// the same coverage but tile into visible lines along the bottom or the top.
    /// </summary>
    public float[] CentroidY { get; }

    public GlyphAtlas(GlyphSet glyphs, Palette palette, int cellWidth, int cellHeight)
    {
        CellWidth = cellWidth;
        CellHeight = cellHeight;
        GlyphCount = glyphs.Count;
        _colourCount = Palette.Count;
        _blockSize = cellWidth * cellHeight;

        _pixels = new int[GlyphCount * _colourCount * _blockSize];

        // Render each glyph once as a white-on-black coverage mask, then tint it per
        // colour. One draw per glyph instead of one per glyph-and-colour, and every
        // colour shares identical antialiasing.
        var masks = RenderMasks(glyphs, cellWidth, cellHeight);

        Coverage = new float[GlyphCount];
        CentroidY = new float[GlyphCount];
        MeasureMasks(masks, cellWidth, cellHeight, Coverage, CentroidY);

        // Normalise the tint so the densest available glyph renders at the palette colour's
        // full value. Without this the whole picture dims as cells get smaller, because a
        // small glyph covers proportionally less of its cell, and a high column count looks
        // washed out for reasons that have nothing to do with the image.
        float peak = 0f;
        foreach (var c in Coverage) peak = Math.Max(peak, c);
        float tintGain = peak > 0.01f ? 1f / peak : 1f;

        for (int g = 0; g < GlyphCount; g++)
        {
            var mask = masks[g];
            for (int b = 0; b < _colourCount; b++)
            {
                var colour = palette[b];
                int offset = Offset(g, b);

                for (int i = 0; i < _blockSize; i++)
                {
                    int coverage = mask[i];
                    if (coverage == 0)
                    {
                        _pixels[offset + i] = unchecked((int)0xFF000000);
                        continue;
                    }

                    float lit = Math.Min(1f, coverage / 255f * tintGain);

                    int r = (int)(colour.R * lit);
                    int gr = (int)(colour.G * lit);
                    int bl = (int)(colour.B * lit);

                    _pixels[offset + i] = unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(gr << 8) | (uint)bl));
                }
            }
        }

        _halfBlocks = BuildHalfBlocks(palette, cellWidth, cellHeight);
    }

    /// <summary>Mean coverage and vertical centre of mass of each rendered glyph.</summary>
    private static void MeasureMasks(
        byte[][] masks, int cellWidth, int cellHeight, float[] coverage, float[] centroidY)
    {
        int blockSize = cellWidth * cellHeight;

        for (int g = 0; g < masks.Length; g++)
        {
            var mask = masks[g];

            double total = 0;
            double weightedRow = 0;

            for (int y = 0; y < cellHeight; y++)
            {
                int row = y * cellWidth;
                double rowSum = 0;
                for (int x = 0; x < cellWidth; x++) rowSum += mask[row + x];

                total += rowSum;
                weightedRow += rowSum * y;
            }

            coverage[g] = (float)(total / (blockSize * 255.0));
            centroidY[g] = total > 0
                ? (float)(weightedRow / total / Math.Max(1, cellHeight - 1))
                : 0.5f;
        }
    }

    /// <summary>
    /// Solid two-colour tiles, one per pair of palette slots: the top half of the cell in
    /// the first colour and the bottom half in the second. Seventeen squared is small
    /// enough to precompute, and drawing one costs exactly what drawing a glyph costs.
    /// </summary>
    private int[] BuildHalfBlocks(Palette palette, int cellWidth, int cellHeight)
    {
        int count = Palette.Count;
        var tiles = new int[count * count * _blockSize];
        int split = cellHeight / 2;

        for (int upper = 0; upper < count; upper++)
        for (int lower = 0; lower < count; lower++)
        {
            var top = palette[upper];
            var bottom = palette[lower];

            int topArgb = unchecked((int)(0xFF000000u | (uint)(top.R << 16) | (uint)(top.G << 8) | top.B));
            int bottomArgb = unchecked((int)(0xFF000000u | (uint)(bottom.R << 16) | (uint)(bottom.G << 8) | bottom.B));

            int offset = HalfBlockOffset(upper, lower);

            for (int y = 0; y < cellHeight; y++)
            {
                int value = y < split ? topArgb : bottomArgb;
                int row = offset + y * cellWidth;
                for (int x = 0; x < cellWidth; x++) tiles[row + x] = value;
            }
        }

        return tiles;
    }

    private int HalfBlockOffset(int upper, int lower)
        => (upper * Palette.Count + lower) * _blockSize;

    /// <summary>The solid tile for a pair of palette slots.</summary>
    public ReadOnlySpan<int> HalfBlock(int upper, int lower)
    {
        if (upper < 0 || upper >= Palette.Count) upper = 0;
        if (lower < 0 || lower >= Palette.Count) lower = 0;
        return _halfBlocks.AsSpan(HalfBlockOffset(upper, lower), _blockSize);
    }

    private int Offset(int glyph, int colour) => (glyph * _colourCount + colour) * _blockSize;

    /// <summary>The pre-rendered pixel block for one glyph in one palette colour.</summary>
    public ReadOnlySpan<int> Block(int glyph, int colour)
    {
        if (glyph < 0 || glyph >= GlyphCount) glyph = GlyphSet.BlankIndex;
        if (colour < 0 || colour >= _colourCount) colour = 0;
        return _pixels.AsSpan(Offset(glyph, colour), _blockSize);
    }

    private static byte[][] RenderMasks(GlyphSet glyphs, int cellWidth, int cellHeight)
    {
        var masks = new byte[glyphs.Count][];

        using var consolas = FitFont("Consolas", cellWidth, cellHeight, 'W');
        using var gothic = FitFont("MS Gothic", cellWidth, cellHeight, 'ﾊ');

        using var bitmap = new Bitmap(cellWidth, cellHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        using var white = new SolidBrush(Color.White);
        using var black = new SolidBrush(Color.Black);

        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;

        using var format = new StringFormat(StringFormat.GenericTypographic)
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
            FormatFlags = StringFormatFlags.NoWrap | StringFormatFlags.NoClip
        };

        var rect = new RectangleF(0, 0, cellWidth, cellHeight);

        for (int i = 0; i < glyphs.Count; i++)
        {
            char c = glyphs.Chars[i];
            var mask = new byte[cellWidth * cellHeight];

            if (c != ' ')
            {
                g.FillRectangle(black, 0, 0, cellWidth, cellHeight);
                var font = IsHalfWidthKatakana(c) ? gothic : consolas;
                g.DrawString(c.ToString(), font, white, rect, format);
                g.Flush();

                var data = bitmap.LockBits(
                    new Rectangle(0, 0, cellWidth, cellHeight),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);
                try
                {
                    unsafe
                    {
                        byte* scan = (byte*)data.Scan0;
                        for (int y = 0; y < cellHeight; y++)
                        {
                            byte* row = scan + y * data.Stride;
                            for (int x = 0; x < cellWidth; x++)
                            {
                                // White on black: any channel is the coverage value.
                                mask[y * cellWidth + x] = row[x * 4 + 1];
                            }
                        }
                    }
                }
                finally
                {
                    bitmap.UnlockBits(data);
                }
            }

            masks[i] = mask;
        }

        return masks;
    }

    /// <summary>
    /// Largest font that keeps a representative glyph inside the cell. Measured rather
    /// than assumed, because font metrics vary and an overflowing glyph would smear
    /// into its neighbours in the atlas.
    /// </summary>
    private static Font FitFont(string family, int cellWidth, int cellHeight, char sample)
    {
        float size = cellHeight;
        Font? best = null;

        using var probe = new Bitmap(1, 1);
        using var g = Graphics.FromImage(probe);

        for (int attempt = 0; attempt < 48 && size >= 4f; attempt++)
        {
            var font = new Font(family, size, FontStyle.Regular, GraphicsUnit.Pixel);
            var measured = g.MeasureString(sample.ToString(), font, PointF.Empty, StringFormat.GenericTypographic);

            if (measured.Width <= cellWidth && measured.Height <= cellHeight)
            {
                best = font;
                break;
            }

            font.Dispose();
            size -= Math.Max(0.5f, size * 0.06f);
        }

        return best ?? new Font(family, Math.Max(4f, cellHeight * 0.7f), FontStyle.Regular, GraphicsUnit.Pixel);
    }

    private static bool IsHalfWidthKatakana(char c) => c >= '｡' && c <= 'ﾟ';

    public void Dispose()
    {
        // Pixel data is managed; nothing unmanaged is held past construction.
    }
}
