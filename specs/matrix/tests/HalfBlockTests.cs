// Traces: MATRIX-HALFBLOCK, MATRIX-DITHER (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Ascii;
using SavviedMatrix.Core;
using SavviedMatrix.Render;

namespace SavviedMatrix.Tests;

public class HalfBlockTests
{
    private static readonly Palette Ansi = new(PaletteMode.Ansi);

    private static int Argb(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    // ------------------------------------------------------------------ half blocks

    [Fact]
    public void EachCellCarriesTwoStackedSamples()
    {
        // Two cell rows, so four sample rows: red, green, blue, white.
        var pixels = new[]
        {
            Argb(200, 0, 0),
            Argb(0, 200, 0),
            Argb(0, 0, 200),
            Argb(255, 255, 255)
        };
        var image = new PixelGrid(pixels, 1, 4);
        var luminance = new[] { 0.6f, 0.6f, 0.6f, 0.9f };
        var placement = new GridPlacement(1, 2, 0, 0);

        var grid = AsciiMapper.MapHalfBlock(image, luminance, placement, 1, 2, Ansi);

        // Upper and lower of the first cell are the first two samples.
        Assert.Equal(Ansi.IndexFor(pixels[0], 0.6f), grid.Colour[grid.Index(0, 0)]);
        Assert.Equal(Ansi.IndexFor(pixels[1], 0.6f), grid.Lower[grid.Index(0, 0)]);
        Assert.Equal(Ansi.IndexFor(pixels[2], 0.6f), grid.Colour[grid.Index(0, 1)]);
    }

    [Fact]
    public void HalfBlockCellsAreMarkedAsSuchAndOrdinaryCellsAreNot()
    {
        var image = new PixelGrid(new[] { Argb(200, 0, 0), Argb(0, 200, 0) }, 1, 2);
        var half = AsciiMapper.MapHalfBlock(image, new[] { 0.6f, 0.6f }, new GridPlacement(1, 1, 0, 0), 1, 1, Ansi);

        Assert.NotEqual(CellGrid.NotHalfBlock, half.Lower[0]);

        var glyphs = new GlyphSet();
        var ordinary = AsciiMapper.Map(
            image, new[] { 0.6f }, new GridPlacement(1, 1, 0, 0), 1, 1,
            GlyphMode.Dense, glyphs, Ansi, new Random(1));

        Assert.Equal(CellGrid.NotHalfBlock, ordinary.Lower[0]);
    }

    [Fact]
    public void CellsOutsideThePlacementAreNotHalfBlocks()
    {
        var image = new PixelGrid(new[] { Argb(200, 0, 0), Argb(0, 200, 0) }, 1, 2);
        var placement = new GridPlacement(1, 1, 2, 1);

        var grid = AsciiMapper.MapHalfBlock(image, new[] { 0.6f, 0.6f }, placement, 5, 3, Ansi);

        for (int row = 0; row < 3; row++)
        for (int col = 0; col < 5; col++)
        {
            if (placement.Contains(col, row)) continue;
            Assert.Equal(CellGrid.NotHalfBlock, grid.Lower[grid.Index(col, row)]);
        }
    }

    [Fact]
    public void AllCellGridsStartOutAsOrdinaryCharacterCells()
    {
        var grid = new CellGrid(4, 4, new GridPlacement(0, 0, 0, 0));

        Assert.All(grid.Lower, v => Assert.Equal(CellGrid.NotHalfBlock, v));
    }

    [Fact]
    public void HalfBlockModeIsRecognisedByAllItsSpellings()
    {
        Assert.Equal(GlyphMode.HalfBlock, GlyphSet.ParseMode("half"));
        Assert.Equal(GlyphMode.HalfBlock, GlyphSet.ParseMode("halfblock"));
        Assert.Equal(GlyphMode.HalfBlock, GlyphSet.ParseMode("half-block"));
        Assert.Equal(GlyphMode.HalfBlock, GlyphSet.ParseMode("HALF"));
        Assert.NotEqual(GlyphMode.HalfBlock, GlyphSet.ParseMode("dense"));
    }

    // ------------------------------------------------------------------ dithering

    [Fact]
    public void ZeroStrengthGivesNoOffsetAnywhere()
    {
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            Assert.Equal(0f, Dither.Offset(x, y, 0.0));
    }

    [Fact]
    public void OffsetsStayInsideHalfTheStrengthEitherWay()
    {
        const double strength = 0.2;

        for (int y = 0; y < 16; y++)
        for (int x = 0; x < 16; x++)
            Assert.InRange(Dither.Offset(x, y, strength), -strength / 2 - 1e-6, strength / 2 + 1e-6);
    }

    [Fact]
    public void TheMatrixAveragesToAboutZeroSoTheImageDoesNotShift()
    {
        double sum = 0;
        for (int y = 0; y < Dither.Size; y++)
        for (int x = 0; x < Dither.Size; x++)
            sum += Dither.Offset(x, y, 1.0);

        // Exactly zero, not merely close: a biased matrix would darken every image.
        Assert.Equal(0.0, sum / (Dither.Size * Dither.Size), 5);
    }

    [Fact]
    public void EveryCellInTheTileGetsADistinctOffset()
    {
        var seen = new HashSet<float>();

        for (int y = 0; y < Dither.Size; y++)
        for (int x = 0; x < Dither.Size; x++)
            seen.Add(Dither.Offset(x, y, 1.0));

        Assert.Equal(Dither.Size * Dither.Size, seen.Count);
    }

    [Fact]
    public void ThePatternRepeatsAndHandlesNegativeCoordinates()
    {
        Assert.Equal(Dither.Offset(3, 5, 0.2), Dither.Offset(3 + Dither.Size, 5 + Dither.Size, 0.2));
        Assert.Equal(Dither.Offset(3, 5, 0.2), Dither.Offset(3 - Dither.Size, 5 - Dither.Size, 0.2));
    }

    [Fact]
    public void DitheringBreaksUpAFlatBandIntoTwoColours()
    {
        // A flat patch sitting just below a threshold. Undithered the whole run picks one
        // slot and any nearby edge is a hard line; dithered, it mixes the two sides.
        var image = new int[64];
        var luminance = new float[64];
        for (int i = 0; i < 64; i++)
        {
            luminance[i] = Palette.BrightThreshold - 0.01f;
            image[i] = Argb(200, 40, 40);
        }

        var grid = new PixelGrid(image, 64, 1);
        var placement = new GridPlacement(64, 1, 0, 0);
        var glyphs = new GlyphSet();

        var flat = AsciiMapper.Map(
            grid, luminance, placement, 64, 1, GlyphMode.Dense, glyphs, Ansi, new Random(1),
            saturationScale: 1f, dither: 0.0);

        var dithered = AsciiMapper.Map(
            grid, luminance, placement, 64, 1, GlyphMode.Dense, glyphs, Ansi, new Random(1),
            saturationScale: 1f, dither: 0.3);

        Assert.Equal(1, flat.Colour.Distinct().Count());
        Assert.Equal(2, dithered.Colour.Distinct().Count());
    }
}
