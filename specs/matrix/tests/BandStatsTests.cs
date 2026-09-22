// Traces: MATRIX-SOUND-BANDS (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Audio;
using SavviedMatrix.Core;

namespace SavviedMatrix.Tests;

public class BandStatsTests
{
    private static int Argb(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    private static PixelGrid Solid(int width, int height, int argb)
    {
        var pixels = new int[width * height];
        Array.Fill(pixels, argb);
        return new PixelGrid(pixels, width, height);
    }

    // ------------------------------------------------------------------ colour space

    [Fact]
    public void PrimariesConvertToTheExpectedHues()
    {
        Assert.Equal(0f, BandAnalyzer.RgbToHsl(1f, 0f, 0f).Hue, 1);
        Assert.Equal(120f, BandAnalyzer.RgbToHsl(0f, 1f, 0f).Hue, 1);
        Assert.Equal(240f, BandAnalyzer.RgbToHsl(0f, 0f, 1f).Hue, 1);
        Assert.Equal(60f, BandAnalyzer.RgbToHsl(1f, 1f, 0f).Hue, 1);
    }

    [Fact]
    public void HueAlwaysLandsInsideZeroToThreeSixty()
    {
        for (int r = 0; r <= 255; r += 51)
        for (int g = 0; g <= 255; g += 51)
        for (int b = 0; b <= 255; b += 51)
        {
            var hue = BandAnalyzer.RgbToHsl(r / 255f, g / 255f, b / 255f).Hue;
            Assert.InRange(hue, 0f, 359.9999f);
        }
    }

    [Fact]
    public void GreysHaveNoSaturation()
    {
        foreach (var v in new[] { 0f, 0.25f, 0.5f, 0.75f, 1f })
        {
            var (_, sat, lum) = BandAnalyzer.RgbToHsl(v, v, v);
            Assert.Equal(0f, sat, 4);
            Assert.Equal(v, lum, 4);
        }
    }

    // ------------------------------------------------------------------ banding

    [Fact]
    public void AFullScreenImageCoversEveryBand()
    {
        var image = Solid(64, 16, Argb(200, 50, 50));
        var placement = new GridPlacement(64, 16, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 64, bands: 8);

        Assert.Equal(8, bands.Length);
        Assert.All(bands, b =>
        {
            Assert.Equal(1f, b.Coverage, 3);
            Assert.False(b.IsRest);
        });
    }

    [Fact]
    public void LetterboxBandsAreRests()
    {
        // A 20-column image centred in a 60-column screen: the outer thirds are letterbox.
        var image = Solid(20, 10, Argb(255, 255, 255));
        var placement = new GridPlacement(20, 10, 20, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 60, bands: 3);

        Assert.True(bands[0].IsRest);
        Assert.False(bands[1].IsRest);
        Assert.True(bands[2].IsRest);
        Assert.Equal(0f, bands[0].Coverage, 3);
        Assert.Equal(1f, bands[1].Coverage, 3);
    }

    [Fact]
    public void ABandStraddlingTheEdgeReportsPartialCoverage()
    {
        // Image occupies columns 10 to 29 of a 40-column screen, split into 4 bands of 10.
        var image = Solid(20, 4, Argb(10, 200, 10));
        var placement = new GridPlacement(20, 4, 10, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 40, bands: 4);

        Assert.Equal(0f, bands[0].Coverage, 3);
        Assert.Equal(1f, bands[1].Coverage, 3);
        Assert.Equal(1f, bands[2].Coverage, 3);
        Assert.Equal(0f, bands[3].Coverage, 3);
    }

    [Fact]
    public void BandsPartitionTheScreenWithoutGapsOrOverlap()
    {
        // Every screen column must belong to exactly one band, for any band count.
        foreach (int bandCount in new[] { 1, 3, 7, 16, 64 })
        {
            int screenCols = 192;
            var covered = new int[screenCols];

            for (int b = 0; b < bandCount; b++)
            {
                int start = (int)((long)b * screenCols / bandCount);
                int end = (int)((long)(b + 1) * screenCols / bandCount);
                for (int c = start; c < end; c++) covered[c]++;
            }

            Assert.All(covered, c => Assert.Equal(1, c));
        }
    }

    // ------------------------------------------------------------------ colour reporting

    [Fact]
    public void ASolidRedImageReportsRedAtFullSaturation()
    {
        var image = Solid(32, 8, Argb(255, 0, 0));
        var placement = new GridPlacement(32, 8, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 32, bands: 4);

        Assert.All(bands, b =>
        {
            Assert.Equal(0f, b.Hue, 1);
            Assert.True(b.Saturation > 0.9f);
            Assert.False(b.IsGreyscale);
        });
    }

    [Fact]
    public void AGreyscaleImageIsReportedAsGreyscale()
    {
        var image = Solid(32, 8, Argb(128, 128, 128));
        var placement = new GridPlacement(32, 8, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 32, bands: 4);

        Assert.All(bands, b =>
        {
            Assert.True(b.IsGreyscale);
            Assert.Equal(0.502f, b.Luminance, 2);
        });
    }

    [Fact]
    public void EachBandReportsTheColourBeneathIt()
    {
        // Left half red, right half blue.
        const int width = 32, height = 4;
        var pixels = new int[width * height];
        for (int y = 0; y < height; y++)
        for (int x = 0; x < width; x++)
            pixels[y * width + x] = x < width / 2 ? Argb(255, 0, 0) : Argb(0, 0, 255);

        var image = new PixelGrid(pixels, width, height);
        var placement = new GridPlacement(width, height, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: width, bands: 2);

        Assert.Equal(0f, bands[0].Hue, 1);
        Assert.Equal(240f, bands[1].Hue, 1);
    }

    [Fact]
    public void ABandWithNoPixelsReportsZeroesRatherThanThrowing()
    {
        var image = Solid(4, 4, Argb(255, 255, 255));
        var placement = new GridPlacement(0, 0, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 40, bands: 4);

        Assert.All(bands, b =>
        {
            Assert.True(b.IsRest);
            Assert.Equal(0f, b.Luminance, 4);
        });
    }

    [Fact]
    public void MoreBandsThanColumnsStillProducesOneEntryPerBand()
    {
        var image = Solid(8, 2, Argb(0, 128, 255));
        var placement = new GridPlacement(8, 2, 0, 0);

        var bands = BandAnalyzer.Analyze(image, placement, screenCols: 8, bands: 32);

        Assert.Equal(32, bands.Length);
    }

    [Fact]
    public void ZeroBandsIsRejected()
    {
        var image = Solid(8, 2, Argb(0, 0, 0));
        var placement = new GridPlacement(8, 2, 0, 0);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => BandAnalyzer.Analyze(image, placement, screenCols: 8, bands: 0));
    }
}
