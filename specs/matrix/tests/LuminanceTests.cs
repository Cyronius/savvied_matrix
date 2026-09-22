// Traces: MATRIX-LUMA-STRETCH (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Ascii;
using SavviedMatrix.Core;

namespace SavviedMatrix.Tests;

public class LuminanceTests
{
    private static int Argb(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    private static PixelGrid Grid(params int[] pixels) => new(pixels, pixels.Length, 1);

    [Fact]
    public void BlackAndWhiteMapToTheEndsOfTheRange()
    {
        var lum = Luminance.Compute(Grid(Argb(0, 0, 0), Argb(255, 255, 255)));

        Assert.Equal(0f, lum[0], 4);
        Assert.Equal(1f, lum[1], 4);
    }

    [Fact]
    public void PrimariesUseRec709Weights()
    {
        var lum = Luminance.Compute(Grid(Argb(255, 0, 0), Argb(0, 255, 0), Argb(0, 0, 255)));

        Assert.Equal(0.2126f, lum[0], 3);
        Assert.Equal(0.7152f, lum[1], 3);
        Assert.Equal(0.0722f, lum[2], 3);
    }

    [Fact]
    public void GreenDominatesRedWhichDominatesBlue()
    {
        var lum = Luminance.Compute(Grid(Argb(255, 0, 0), Argb(0, 255, 0), Argb(0, 0, 255)));

        Assert.True(lum[1] > lum[0]);
        Assert.True(lum[0] > lum[2]);
    }

    [Fact]
    public void FlatImageBecomesUniformMidGreyRatherThanDividingByZero()
    {
        var flat = Enumerable.Repeat(0.42f, 200).ToArray();

        var stretched = Luminance.ContrastStretch(flat);

        Assert.All(stretched, v => Assert.Equal(0.5f, v, 4));
    }

    [Fact]
    public void AllBlackImageIsAlsoTreatedAsFlat()
    {
        var black = new float[200];

        var stretched = Luminance.ContrastStretch(black);

        Assert.All(stretched, v => Assert.Equal(0.5f, v, 4));
    }

    [Fact]
    public void StretchExpandsANarrowBandToTheFullRange()
    {
        // 100 values evenly spread over the narrow band 0.40 to 0.60.
        var values = Enumerable.Range(0, 100).Select(i => 0.40f + 0.20f * i / 99f).ToArray();

        var stretched = Luminance.ContrastStretch(values);

        Assert.Equal(0f, stretched.Min(), 3);
        Assert.Equal(1f, stretched.Max(), 3);
        Assert.True(stretched[50] > 0.4f && stretched[50] < 0.6f);
    }

    [Fact]
    public void StretchIsMonotonic()
    {
        var values = Enumerable.Range(0, 100).Select(i => i / 99f).ToArray();

        var stretched = Luminance.ContrastStretch(values);

        for (int i = 1; i < stretched.Length; i++)
            Assert.True(stretched[i] >= stretched[i - 1], $"not monotonic at {i}");
    }

    [Fact]
    public void OutliersAreClampedRatherThanCompressingEverythingElse()
    {
        // 98 mid values with one extreme outlier at each end.
        var values = new float[100];
        for (int i = 1; i < 99; i++) values[i] = 0.5f + (i - 50) * 0.0001f;
        values[0] = 0f;
        values[99] = 1f;

        var stretched = Luminance.ContrastStretch(values);

        Assert.Equal(0f, stretched[0], 4);
        Assert.Equal(1f, stretched[99], 4);
        // The bulk still spans the range rather than collapsing around 0.5.
        Assert.True(stretched.Skip(1).Take(98).Max() - stretched.Skip(1).Take(98).Min() > 0.5f);
    }

    [Fact]
    public void ResultsAlwaysStayWithinTheUnitRange()
    {
        var values = Enumerable.Range(0, 500).Select(i => (i % 7) / 6f).ToArray();

        var stretched = Luminance.ContrastStretch(values, gamma: 1.8);

        Assert.All(stretched, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void GammaAboveOneDarkensTheMidTones()
    {
        var values = Enumerable.Range(0, 100).Select(i => i / 99f).ToArray();

        var plain = Luminance.ContrastStretch(values);
        var gammad = Luminance.ContrastStretch(values, gamma: 2.0);

        Assert.True(gammad[50] < plain[50]);
        Assert.Equal(plain[0], gammad[0], 3);
        Assert.Equal(plain[99], gammad[99], 3);
    }

    [Fact]
    public void EmptyInputProducesEmptyOutput()
    {
        Assert.Empty(Luminance.ContrastStretch(Array.Empty<float>()));
    }

    [Fact]
    public void PercentilePicksTheExpectedRanks()
    {
        var sorted = Enumerable.Range(0, 101).Select(i => i / 100f).ToArray();

        Assert.Equal(0f, Luminance.Percentile(sorted, 0.0), 4);
        Assert.Equal(0.5f, Luminance.Percentile(sorted, 0.5), 4);
        Assert.Equal(1f, Luminance.Percentile(sorted, 1.0), 4);
    }
}
