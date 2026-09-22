// Traces: MATRIX-PALETTE (canonical spec: specs/matrix/spec.md)
using System.Drawing;
using SavviedMatrix.Ascii;

namespace SavviedMatrix.Tests;

public class PaletteTests
{
    private static readonly Palette Matrix = new(PaletteMode.Matrix);
    private static readonly Palette Ansi = new(PaletteMode.Ansi);

    [Fact]
    public void BothPalettesHaveSixteenSlotsPlusAHighlight()
    {
        Assert.Equal(17, Palette.Count);
        Assert.Equal(16, Palette.ShadeCount);
        Assert.Equal(16, Palette.HighlightIndex);

        foreach (var palette in new[] { Matrix, Ansi })
            Assert.Equal(Palette.Count, palette.Colors.Length);
    }

    [Fact]
    public void ParseModeDefaultsToAnsiAndRecognisesMatrix()
    {
        Assert.Equal(PaletteMode.Matrix, Palette.ParseMode("matrix"));
        Assert.Equal(PaletteMode.Matrix, Palette.ParseMode("MATRIX"));
        Assert.Equal(PaletteMode.Ansi, Palette.ParseMode("ansi"));
        Assert.Equal(PaletteMode.Ansi, Palette.ParseMode(null));
        Assert.Equal(PaletteMode.Ansi, Palette.ParseMode("nonsense"));
    }

    // ------------------------------------------------------------------ matrix palette

    [Fact]
    public void MatrixSlotsAreGreenAndGetBrighterWithIndex()
    {
        for (int i = 0; i < Palette.ShadeCount; i++)
        {
            var c = Matrix[i];
            Assert.True(c.G >= c.R, $"slot {i} is not green-dominant");
            Assert.True(c.G >= c.B, $"slot {i} is not green-dominant");
        }

        for (int i = 1; i < Palette.ShadeCount; i++)
            Assert.True(Matrix[i].G >= Matrix[i - 1].G, $"slot {i} is darker than {i - 1}");
    }

    [Fact]
    public void MatrixDarkestSlotIsNotPureBlackSoShadowsStillReadAsText()
    {
        var darkest = Matrix[0];

        Assert.NotEqual(Color.Black.ToArgb(), darkest.ToArgb());
        Assert.True(darkest.G > 0);
    }

    [Fact]
    public void MatrixShadeIndexSpansAllSixteenSlots()
    {
        Assert.Equal(0, Matrix.ShadeIndex(0f));
        Assert.Equal(15, Matrix.ShadeIndex(1f));

        var seen = Enumerable.Range(0, 256).Select(i => Matrix.ShadeIndex(i / 255f)).Distinct().ToList();
        Assert.Equal(16, seen.Count);
    }

    [Fact]
    public void MatrixColourIgnoresHueAndFollowsBrightness()
    {
        // Same brightness, wildly different hues, identical slot.
        byte red = Matrix.IndexFor(unchecked((int)0xFFFF0000u), 0.5f, 0.5f);
        byte blue = Matrix.IndexFor(unchecked((int)0xFF0000FFu), 0.5f, 0.5f);

        Assert.Equal(red, blue);
        Assert.Equal(Matrix.ShadeIndex(0.5f), red);
    }

    // ------------------------------------------------------------------ ansi palette

    [Fact]
    public void AnsiSlotsAreTheStandardSixteen()
    {
        Assert.Equal(Color.FromArgb(0, 0, 0).ToArgb(), Ansi[0].ToArgb());
        Assert.Equal(Color.FromArgb(0xAA, 0x00, 0x00).ToArgb(), Ansi[1].ToArgb());
        Assert.Equal(Color.FromArgb(0x00, 0xAA, 0x00).ToArgb(), Ansi[2].ToArgb());
        Assert.Equal(Color.FromArgb(0x00, 0x00, 0xAA).ToArgb(), Ansi[4].ToArgb());
        Assert.Equal(Color.FromArgb(0x55, 0xFF, 0x55).ToArgb(), Ansi[10].ToArgb());
        Assert.Equal(Color.FromArgb(0xFF, 0xFF, 0xFF).ToArgb(), Ansi[15].ToArgb());
    }

    [Theory]
    [InlineData(0, 0, 0, 0)]        // black
    [InlineData(170, 0, 0, 1)]      // red
    [InlineData(0, 170, 0, 2)]      // green
    [InlineData(0, 0, 170, 4)]      // blue
    [InlineData(255, 255, 255, 15)] // white
    [InlineData(85, 255, 85, 10)]   // bright green
    public void NearestAnsiReturnsAnExactMatchForAPaletteColourItself(int r, int g, int b, int expected)
    {
        Assert.Equal(expected, Palette.NearestAnsi(r, g, b));
    }

    [Fact]
    public void NearestAnsiAlwaysReturnsAValidSlot()
    {
        var rng = new Random(11);
        for (int i = 0; i < 2000; i++)
        {
            byte slot = Palette.NearestAnsi(rng.Next(256), rng.Next(256), rng.Next(256));
            Assert.InRange(slot, 0, Palette.ShadeCount - 1);
        }
    }

    [Fact]
    public void AnsiColourFollowsHueRatherThanBrightnessAlone()
    {
        byte red = Ansi.IndexFor(unchecked((int)0xFFCC0000u), 0.35f);
        byte blue = Ansi.IndexFor(unchecked((int)0xFF0000CCu), 0.35f);

        Assert.NotEqual(red, blue);
    }

    [Theory]
    [InlineData(0xFFCC0000u, 1, 9)]   // red
    [InlineData(0xFFCCCC00u, 3, 11)]  // yellow
    [InlineData(0xFF00CC00u, 2, 10)]  // green
    [InlineData(0xFF00CCCCu, 6, 14)]  // cyan
    [InlineData(0xFF0000CCu, 4, 12)]  // blue
    [InlineData(0xFFCC00CCu, 5, 13)]  // magenta
    public void EachHueSectorHasADimAndABrightSlot(uint argb, int dim, int bright)
    {
        // Brightness is carried by the choice of character, so the sixteen slots are free
        // to carry hue: every sector gets a dim and a bright variant and nothing else.
        Assert.Equal(dim, Ansi.IndexFor(unchecked((int)argb), 0.30f));
        Assert.Equal(bright, Ansi.IndexFor(unchecked((int)argb), 0.70f));
    }

    [Fact]
    public void AWashedOutCellFallsBackToGrey()
    {
        // Barely any colour in it, so it should read as a neutral rather than be forced
        // into a hue.
        int nearlyGrey = unchecked((int)0xFF807A78u);

        byte slot = Ansi.IndexFor(nearlyGrey, 0.50f);

        Assert.Contains(slot, new byte[] { 0, 7, 8, 15 });
    }

    [Fact]
    public void TheSaturationScaleIsWhatPushesAMutedCellIntoAHue()
    {
        // This is the whole point of normalising saturation: a muted cell that would have
        // read as grey gets a hue once the image's own colour range has been stretched.
        int muted = unchecked((int)0xFF8C7A7Au);

        byte unboosted = Ansi.IndexFor(muted, 0.50f, saturationScale: 1f);
        byte boosted = Ansi.IndexFor(muted, 0.50f, saturationScale: 4f);

        Assert.Contains(unboosted, new byte[] { 0, 7, 8, 15 });
        Assert.DoesNotContain(boosted, new byte[] { 0, 7, 8, 15 });
    }

    [Fact]
    public void AVeryDarkCellIsBlackWhateverColourItStartedAs()
    {
        Assert.Equal(0, Ansi.IndexFor(unchecked((int)0xFFFF0000u), 0.05f));
        Assert.Equal(0, Ansi.IndexFor(unchecked((int)0xFF804020u), 0.0f));
    }

    // ------------------------------------------------------------------ rain trail

    [Fact]
    public void TheTrailIsBrightestAtTheHeadAndDarkestAtTheTail()
    {
        foreach (var palette in new[] { Matrix, Ansi })
        {
            Assert.Equal(Palette.HighlightIndex, palette.TrailIndex(1.0));
            Assert.Equal(0, palette.TrailIndex(0.0));
        }
    }

    [Fact]
    public void TheTrailFadesMonotonicallyInBrightness()
    {
        foreach (var palette in new[] { Matrix, Ansi })
        {
            double previous = double.MaxValue;

            for (int step = 10; step >= 0; step--)
            {
                var c = palette[palette.TrailIndex(step / 10.0)];
                double brightness = 0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B;

                Assert.True(
                    brightness <= previous + 0.5,
                    $"{palette.Mode} trail brightened going down the tail at step {step}");
                previous = brightness;
            }
        }
    }

    [Fact]
    public void TheAnsiTrailStaysGreenOrGreyRatherThanCyclingThroughHues()
    {
        for (int step = 0; step <= 10; step++)
        {
            var c = Ansi[Ansi.TrailIndex(step / 10.0)];

            // Green-dominant, or a neutral grey. Never red, blue, magenta and so on.
            bool greenish = c.G >= c.R && c.G >= c.B;
            bool neutral = c.R == c.G && c.G == c.B;

            Assert.True(greenish || neutral, $"trail step {step} was {c}");
        }
    }

    [Fact]
    public void TrailIndexClampsOutOfRangeInput()
    {
        foreach (var palette in new[] { Matrix, Ansi })
        {
            Assert.Equal(palette.TrailIndex(1.0), palette.TrailIndex(5.0));
            Assert.Equal(palette.TrailIndex(0.0), palette.TrailIndex(-5.0));
        }
    }

    [Fact]
    public void IndexerClampsOutOfRangeInput()
    {
        Assert.Equal(Matrix[0].ToArgb(), Matrix[-3].ToArgb());
        Assert.Equal(Matrix[Palette.Count - 1].ToArgb(), Matrix[99].ToArgb());
    }
}
