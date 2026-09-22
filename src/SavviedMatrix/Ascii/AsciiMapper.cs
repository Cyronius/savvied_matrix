using SavviedMatrix.Core;
using SavviedMatrix.Render;

namespace SavviedMatrix.Ascii;

/// <summary>
/// Turns a downsampled image into the character and colour index each cell will draw.
/// The character carries brightness; the colour index carries hue in ANSI mode and
/// brightness again in Matrix mode.
/// </summary>
public static class AsciiMapper
{
    /// <summary>Katakana cells darker than this are left blank, so shadows stay black.</summary>
    public const float KatakanaBlankThreshold = 0.08f;

    /// <summary>Fraction of the brightest cells promoted to the highlight colour.</summary>
    public const double HeadFraction = 0.02;

    /// <summary>
    /// Maps an image, already downsampled to
    /// <paramref name="placement"/>.FitCols x FitRows, onto a full screen grid.
    /// Cells outside the placement are left blank and black.
    /// </summary>
    public static CellGrid Map(
        PixelGrid image,
        float[] stretchedLuminance,
        GridPlacement placement,
        int cols,
        int rows,
        GlyphMode mode,
        GlyphSet glyphs,
        Palette palette,
        Random rng,
        float saturationScale = 1f,
        double dither = 0.0)
    {
        var grid = new CellGrid(cols, rows, placement);

        float headThreshold = HeadThreshold(stretchedLuminance);

        int[] ramp = glyphs.RampFor(mode);
        int[] kata = glyphs.KatakanaIndices;

        for (int y = 0; y < placement.FitRows; y++)
        {
            int screenRow = placement.OffsetRow + y;
            if (screenRow < 0 || screenRow >= rows) continue;

            for (int x = 0; x < placement.FitCols; x++)
            {
                int screenCol = placement.OffsetCol + x;
                if (screenCol < 0 || screenCol >= cols) continue;

                int src = y * placement.FitCols + x;
                if (src >= stretchedLuminance.Length) continue;

                float lit = Math.Clamp(stretchedLuminance[src], 0f, 1f);

                byte glyph;
                if (mode == GlyphMode.Katakana)
                {
                    glyph = lit < KatakanaBlankThreshold
                        ? GlyphSet.BlankIndex
                        : (byte)kata[rng.Next(kata.Length)];
                }
                else
                {
                    int rampIndex = (int)Math.Round(lit * (ramp.Length - 1), MidpointRounding.AwayFromZero);
                    glyph = (byte)ramp[Math.Clamp(rampIndex, 0, ramp.Length - 1)];
                }

                byte colour;
                if (lit >= headThreshold && headThreshold > 0f)
                {
                    colour = Palette.HighlightIndex;
                }
                else
                {
                    int argb = (x < image.Width && y < image.Height) ? image[x, y] : 0;
                    colour = palette.IndexFor(argb, lit, saturationScale, Dither.Offset(x, y, dither));
                }

                int dst = grid.Index(screenCol, screenRow);
                grid.Glyph[dst] = glyph;
                grid.Colour[dst] = colour;
            }
        }

        return grid;
    }

    /// <summary>
    /// Maps an image sampled at twice the vertical resolution into half-block cells: each
    /// cell takes the colour of two stacked samples, the top half and the bottom half.
    ///
    /// <paramref name="image"/> and <paramref name="stretchedLuminance"/> are
    /// <paramref name="placement"/>.FitCols wide and twice FitRows tall.
    /// </summary>
    public static CellGrid MapHalfBlock(
        PixelGrid image,
        float[] stretchedLuminance,
        GridPlacement placement,
        int cols,
        int rows,
        Palette palette,
        float saturationScale = 1f,
        double dither = 0.0)
    {
        var grid = new CellGrid(cols, rows, placement);

        int sampleRows = placement.FitRows * 2;

        for (int y = 0; y < placement.FitRows; y++)
        {
            int screenRow = placement.OffsetRow + y;
            if (screenRow < 0 || screenRow >= rows) continue;

            for (int x = 0; x < placement.FitCols; x++)
            {
                int screenCol = placement.OffsetCol + x;
                if (screenCol < 0 || screenCol >= cols) continue;

                int dst = grid.Index(screenCol, screenRow);

                grid.Glyph[dst] = GlyphSet.BlankIndex;
                grid.Colour[dst] = SampleColour(image, stretchedLuminance, placement, x, y * 2, sampleRows, palette, saturationScale, dither);
                grid.Lower[dst] = SampleColour(image, stretchedLuminance, placement, x, y * 2 + 1, sampleRows, palette, saturationScale, dither);
            }
        }

        return grid;
    }

    private static byte SampleColour(
        PixelGrid image, float[] luminance, GridPlacement placement,
        int x, int sampleRow, int sampleRows, Palette palette, float saturationScale, double dither)
    {
        if (sampleRow >= sampleRows) return 0;

        int src = sampleRow * placement.FitCols + x;
        if (src >= luminance.Length) return 0;

        float lit = Math.Clamp(luminance[src], 0f, 1f);
        int argb = (x < image.Width && sampleRow < image.Height) ? image[x, sampleRow] : 0;

        return palette.IndexFor(argb, lit, saturationScale, Dither.Offset(x, sampleRow, dither));
    }

    /// <summary>
    /// Brightness at or above which a cell gets the highlight colour, being the top
    /// <see cref="HeadFraction"/> of cells. Returns 0 when the image is too small or
    /// too flat for a highlight to mean anything.
    /// </summary>
    public static float HeadThreshold(float[] luminance)
    {
        if (luminance.Length < 50) return 0f;

        var sorted = (float[])luminance.Clone();
        Array.Sort(sorted);

        float threshold = Luminance.Percentile(sorted, 1.0 - HeadFraction);

        // A threshold of zero would promote every black cell to the highlight colour.
        if (threshold <= 0f) return 0f;

        // A flat image has its 98th percentile equal to everything else, so every cell
        // would qualify as the brightest two per cent and the whole picture would come out
        // in the highlight colour. Require the threshold to actually single out a minority.
        return threshold > Luminance.Percentile(sorted, 0.5) ? threshold : 0f;
    }
}
