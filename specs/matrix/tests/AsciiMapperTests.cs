// Traces: MATRIX-GLYPH-MAP (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Ascii;
using SavviedMatrix.Core;
using SavviedMatrix.Render;

namespace SavviedMatrix.Tests;

public class AsciiMapperTests
{
    private static readonly GlyphSet Glyphs = new();
    private static readonly Palette Matrix = new(PaletteMode.Matrix);
    private static readonly Palette Ansi = new(PaletteMode.Ansi);

    private static int Argb(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    /// <summary>A grey image whose luminance equals the given values.</summary>
    private static PixelGrid GreyImage(float[] luminance, int width, int height)
    {
        var pixels = new int[width * height];
        for (int i = 0; i < pixels.Length && i < luminance.Length; i++)
        {
            int v = (int)Math.Round(Math.Clamp(luminance[i], 0f, 1f) * 255);
            pixels[i] = Argb(v, v, v);
        }
        return new PixelGrid(pixels, width, height);
    }

    private static CellGrid Map(
        float[] luminance, GridPlacement placement, int cols, int rows,
        GlyphMode mode, Palette? palette = null, int seed = 1, PixelGrid? image = null)
    {
        var img = image ?? GreyImage(luminance, placement.FitCols, placement.FitRows);
        return AsciiMapper.Map(
            img, luminance, placement, cols, rows,
            mode, Glyphs, palette ?? Matrix, new Random(seed));
    }

    private static char GlyphChar(CellGrid grid, int col, int row)
        => Glyphs.Chars[grid.Glyph[grid.Index(col, row)]];

    // ------------------------------------------------------------------ glyph selection

    [Fact]
    public void RampMapsDarkToSpaceAndBrightToTheDensestGlyph()
    {
        var grid = Map(new[] { 0f, 1f }, new GridPlacement(2, 1, 0, 0), 2, 1, GlyphMode.Ramp);

        Assert.Equal(' ', GlyphChar(grid, 0, 0));
        Assert.Equal('@', GlyphChar(grid, 1, 0));
    }

    [Fact]
    public void RampIndexRoundsToTheNearestStep()
    {
        // Ten ramp steps, so each step spans a ninth of the range.
        var grid = Map(new[] { 1f / 9f, 4f / 9f, 8f / 9f }, new GridPlacement(3, 1, 0, 0), 3, 1, GlyphMode.Ramp);

        Assert.Equal(GlyphSet.Ramp[1], GlyphChar(grid, 0, 0));
        Assert.Equal(GlyphSet.Ramp[4], GlyphChar(grid, 1, 0));
        Assert.Equal(GlyphSet.Ramp[8], GlyphChar(grid, 2, 0));
    }

    [Fact]
    public void TheDenseRampGivesFarMoreDistinctStepsThanTheShortOne()
    {
        var values = Enumerable.Range(0, 64).Select(i => i / 63f).ToArray();
        var placement = new GridPlacement(64, 1, 0, 0);

        var shortRamp = Map(values, placement, 64, 1, GlyphMode.Ramp);
        var denseRamp = Map(values, placement, 64, 1, GlyphMode.Dense);

        int shortDistinct = shortRamp.Glyph.Distinct().Count();
        int denseDistinct = denseRamp.Glyph.Distinct().Count();

        Assert.Equal(10, shortDistinct);
        Assert.True(denseDistinct > 40, $"expected a fine gradation, got {denseDistinct} distinct glyphs");
    }

    [Fact]
    public void DenseAndBlockRampsStillPutSpaceAtBlackAndTheirDensestGlyphAtWhite()
    {
        foreach (var mode in new[] { GlyphMode.Ramp, GlyphMode.Dense, GlyphMode.Blocks })
        {
            var grid = Map(new[] { 0f, 1f }, new GridPlacement(2, 1, 0, 0), 2, 1, mode);
            var ramp = Glyphs.RampFor(mode);

            Assert.Equal(' ', GlyphChar(grid, 0, 0));
            Assert.Equal(Glyphs.Chars[ramp[^1]], GlyphChar(grid, 1, 0));
        }
    }

    [Fact]
    public void BlocksModeUsesOnlyShadingBlocks()
    {
        var values = Enumerable.Range(0, 40).Select(i => i / 39f).ToArray();
        var grid = Map(values, new GridPlacement(40, 1, 0, 0), 40, 1, GlyphMode.Blocks);

        var allowed = GlyphSet.Blocks.ToHashSet();
        Assert.All(grid.Glyph, g => Assert.Contains(Glyphs.Chars[g], allowed));
    }

    [Fact]
    public void KatakanaLeavesVeryDarkCellsBlank()
    {
        var grid = Map(new[] { 0.05f, 0.5f }, new GridPlacement(2, 1, 0, 0), 2, 1, GlyphMode.Katakana, seed: 7);

        Assert.Equal(' ', GlyphChar(grid, 0, 0));
        Assert.NotEqual(' ', GlyphChar(grid, 1, 0));
    }

    [Fact]
    public void KatakanaThresholdIsInclusiveOfTheBoundary()
    {
        var values = new[] { AsciiMapper.KatakanaBlankThreshold - 0.001f, AsciiMapper.KatakanaBlankThreshold };
        var grid = Map(values, new GridPlacement(2, 1, 0, 0), 2, 1, GlyphMode.Katakana, seed: 7);

        Assert.Equal(' ', GlyphChar(grid, 0, 0));
        Assert.NotEqual(' ', GlyphChar(grid, 1, 0));
    }

    [Fact]
    public void KatakanaOnlyEverUsesKatakanaGlyphs()
    {
        var values = Enumerable.Range(0, 400).Select(i => 0.2f + 0.8f * (i % 50) / 49f).ToArray();
        var grid = Map(values, new GridPlacement(40, 10, 0, 0), 40, 10, GlyphMode.Katakana, seed: 3);

        var allowed = Glyphs.KatakanaIndices.Append((int)GlyphSet.BlankIndex).ToHashSet();
        Assert.All(grid.Glyph, g => Assert.Contains((int)g, allowed));
    }

    // ------------------------------------------------------------------ placement

    [Fact]
    public void CellsOutsideThePlacementStayBlankAndBlack()
    {
        // A 2x1 image centred inside a 6x3 screen grid.
        var placement = new GridPlacement(2, 1, 2, 1);
        var grid = Map(new[] { 1f, 1f }, placement, 6, 3, GlyphMode.Ramp);

        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 6; col++)
        {
            if (placement.Contains(col, row)) continue;

            int i = grid.Index(col, row);
            Assert.Equal(GlyphSet.BlankIndex, grid.Glyph[i]);
            Assert.Equal(0, grid.Colour[i]);
        }

        Assert.NotEqual(GlyphSet.BlankIndex, grid.Glyph[grid.Index(2, 1)]);
    }

    // ------------------------------------------------------------------ colour

    [Fact]
    public void MatrixPaletteColoursByBrightnessAlone()
    {
        var values = new[] { 0f, 0.5f, 1f };
        var placement = new GridPlacement(3, 1, 0, 0);

        // Three different hues at the same brightness must still come out identical.
        var pixels = new[] { Argb(0, 0, 0), Argb(255, 0, 0), Argb(0, 0, 255) };
        var image = new PixelGrid(pixels, 3, 1);

        var grid = Map(values, placement, 3, 1, GlyphMode.Ramp, Matrix, image: image);

        Assert.Equal(Matrix.ShadeIndex(0f), grid.Colour[grid.Index(0, 0)]);
        Assert.Equal(Matrix.ShadeIndex(0.5f), grid.Colour[grid.Index(1, 0)]);
    }

    [Fact]
    public void AnsiPaletteColoursByHue()
    {
        // Mid-brightness red, green and blue must land on three different ANSI slots.
        var values = new[] { 0.5f, 0.5f, 0.5f };
        var placement = new GridPlacement(3, 1, 0, 0);
        var image = new PixelGrid(new[] { Argb(200, 0, 0), Argb(0, 200, 0), Argb(0, 0, 200) }, 3, 1);

        var grid = Map(values, placement, 3, 1, GlyphMode.Ramp, Ansi, image: image);

        var colours = new[]
        {
            grid.Colour[grid.Index(0, 0)],
            grid.Colour[grid.Index(1, 0)],
            grid.Colour[grid.Index(2, 0)]
        };

        // Bright variants, since the cells are above the brightness split.
        Assert.Equal(new byte[] { 9, 10, 12 }, colours);
    }

    [Fact]
    public void EveryColourIndexIsWithinThePalette()
    {
        var values = Enumerable.Range(0, 600).Select(i => (i % 37) / 36f).ToArray();
        var placement = new GridPlacement(60, 10, 0, 0);

        var rng = new Random(4);
        var pixels = Enumerable.Range(0, 600).Select(_ => Argb(rng.Next(256), rng.Next(256), rng.Next(256))).ToArray();
        var image = new PixelGrid(pixels, 60, 10);

        foreach (var palette in new[] { Matrix, Ansi })
        {
            var grid = Map(values, placement, 60, 10, GlyphMode.Dense, palette, image: image);
            Assert.All(grid.Colour, c => Assert.InRange(c, 0, Palette.Count - 1));
        }
    }

    // ------------------------------------------------------------------ highlight

    [Fact]
    public void BrightestCellsArePromotedToTheHighlightColour()
    {
        var values = new float[100];
        for (int i = 0; i < 96; i++) values[i] = 0.2f;
        for (int i = 96; i < 100; i++) values[i] = 1.0f;

        var grid = Map(values, new GridPlacement(100, 1, 0, 0), 100, 1, GlyphMode.Ramp);

        Assert.Equal(Palette.HighlightIndex, grid.Colour[grid.Index(99, 0)]);
        Assert.NotEqual(Palette.HighlightIndex, grid.Colour[grid.Index(0, 0)]);
    }

    [Fact]
    public void HighlightIsSuppressedForTinyGrids()
    {
        Assert.Equal(0f, AsciiMapper.HeadThreshold(Enumerable.Repeat(1f, 10).ToArray()));
    }

    [Fact]
    public void HighlightIsSuppressedForAnAllBlackImage()
    {
        Assert.Equal(0f, AsciiMapper.HeadThreshold(new float[200]));
    }

    [Fact]
    public void AllBlackImageProducesNoHighlightCells()
    {
        var grid = Map(new float[200], new GridPlacement(200, 1, 0, 0), 200, 1, GlyphMode.Ramp);

        Assert.DoesNotContain(Palette.HighlightIndex, grid.Colour);
    }

    [Fact]
    public void MappingIsDeterministicForAGivenSeed()
    {
        var values = Enumerable.Range(0, 100).Select(i => i / 99f).ToArray();
        var placement = new GridPlacement(20, 5, 0, 0);

        var a = Map(values, placement, 20, 5, GlyphMode.Katakana, seed: 99);
        var b = Map(values, placement, 20, 5, GlyphMode.Katakana, seed: 99);

        Assert.Equal(a.Glyph, b.Glyph);
        Assert.Equal(a.Colour, b.Colour);
    }
}
