// Traces: MATRIX-TONE (canonical spec: specs/matrix/spec.md)
using SavviedMatrix.Ascii;
using SavviedMatrix.Core;
using SavviedMatrix.Render;

namespace SavviedMatrix.Tests;

public class ToneTests
{
    private static int Argb(int r, int g, int b)
        => unchecked((int)(0xFF000000u | (uint)(r << 16) | (uint)(g << 8) | (uint)b));

    private static double Entropy(float[] values, int bins = 32)
    {
        var counts = new int[bins];
        foreach (var v in values) counts[(int)(Math.Clamp(v, 0f, 1f) * (bins - 1))]++;

        double total = values.Length;
        double h = 0;
        foreach (var c in counts)
        {
            if (c == 0) continue;
            double p = c / total;
            h -= p * Math.Log2(p);
        }
        return h;
    }

    // ------------------------------------------------------------------ equalisation

    [Fact]
    public void EqualisationSpreadsAPileUpAcrossTheWholeRange()
    {
        // 90% of the image crammed into the bottom tenth, which is what a dark
        // photograph looks like and why it comes out as a silhouette.
        var values = new float[1000];
        for (int i = 0; i < 900; i++) values[i] = 0.02f + 0.08f * i / 899f;
        for (int i = 900; i < 1000; i++) values[i] = 0.5f + 0.5f * (i - 900) / 99f;

        var equalised = Luminance.Equalize(values, 1.0);

        Assert.True(
            Entropy(equalised) > Entropy(values) + 1.0,
            $"entropy only went from {Entropy(values):F2} to {Entropy(equalised):F2}");
    }

    [Fact]
    public void EqualisationIsMonotonic()
    {
        var values = Enumerable.Range(0, 500).Select(i => (float)Math.Pow(i / 499.0, 3)).ToArray();

        var equalised = Luminance.Equalize(values, 1.0);

        var pairs = values.Zip(equalised).OrderBy(p => p.First).ToArray();
        for (int i = 1; i < pairs.Length; i++)
            Assert.True(pairs[i].Second >= pairs[i - 1].Second - 1e-5f, $"order broke at {i}");
    }

    [Fact]
    public void ZeroStrengthLeavesTheValuesAlone()
    {
        var values = Enumerable.Range(0, 100).Select(i => i / 99f).ToArray();

        var equalised = Luminance.Equalize(values, 0.0);

        Assert.Equal(values, equalised);
    }

    [Fact]
    public void PartialStrengthLandsBetweenTheInputAndFullEqualisation()
    {
        var values = Enumerable.Range(0, 500).Select(i => (float)Math.Pow(i / 499.0, 3)).ToArray();

        var full = Luminance.Equalize(values, 1.0);
        var half = Luminance.Equalize(values, 0.5);

        for (int i = 0; i < values.Length; i++)
        {
            float low = Math.Min(values[i], full[i]);
            float high = Math.Max(values[i], full[i]);
            Assert.InRange(half[i], low - 1e-5f, high + 1e-5f);
        }
    }

    [Fact]
    public void EqualisationStaysInRangeAndHandlesDegenerateInput()
    {
        Assert.Empty(Luminance.Equalize(Array.Empty<float>(), 1.0));

        var flat = Enumerable.Repeat(0.3f, 50).ToArray();
        var equalised = Luminance.Equalize(flat, 1.0);
        Assert.All(equalised, v => Assert.InRange(v, 0f, 1f));
    }

    // ------------------------------------------------------------------ local contrast

    [Fact]
    public void LocalContrastPushesACellAwayFromItsNeighbourhood()
    {
        // A dim square sitting on a dark field: the square should separate further.
        const int w = 40, h = 40;
        var values = new float[w * h];
        Array.Fill(values, 0.20f);
        for (int y = 15; y < 25; y++)
        for (int x = 15; x < 25; x++)
            values[y * w + x] = 0.30f;

        var result = LocalContrast.Apply(values, w, h, 1.0);

        float insideBefore = values[20 * w + 20], insideAfter = result[20 * w + 20];
        float outsideBefore = values[2 * w + 2], outsideAfter = result[2 * w + 2];

        Assert.True(insideAfter - outsideAfter > insideBefore - outsideBefore,
            "the square did not separate from its surroundings");
    }

    [Fact]
    public void LocalContrastOfZeroIsTheIdentity()
    {
        var values = Enumerable.Range(0, 100).Select(i => i / 99f).ToArray();

        Assert.Same(values, LocalContrast.Apply(values, 10, 10, 0.0));
    }

    [Fact]
    public void LocalContrastStaysInRange()
    {
        var rng = new Random(3);
        var values = Enumerable.Range(0, 900).Select(_ => (float)rng.NextDouble()).ToArray();

        var result = LocalContrast.Apply(values, 30, 30, 2.0);

        Assert.All(result, v => Assert.InRange(v, 0f, 1f));
    }

    [Fact]
    public void BlurringAUniformFieldChangesNothing()
    {
        var values = Enumerable.Repeat(0.4f, 400).ToArray();

        var blurred = LocalContrast.Blur(values, 20, 20, 3);

        Assert.All(blurred, v => Assert.Equal(0.4f, v, 4));
    }

    // ------------------------------------------------------------------ saturation

    [Fact]
    public void AMutedImageIsScaledUpAndAVividOneIsLeftAlone()
    {
        var muted = Enumerable.Repeat(0.18f, 500).ToArray();
        var vivid = Enumerable.Repeat(0.90f, 500).ToArray();

        float mutedScale = Saturation.ScaleFromSamples(muted);
        float vividScale = Saturation.ScaleFromSamples(vivid);

        Assert.True(mutedScale > 2f, $"muted image only scaled by {mutedScale:F2}");
        Assert.Equal(1f, vividScale, 3);
    }

    [Fact]
    public void ScalingAimsTheMedianAtTheTarget()
    {
        var samples = Enumerable.Range(0, 1000).Select(i => 0.05f + 0.3f * i / 999f).ToArray();

        float scale = Saturation.ScaleFromSamples(samples);
        float median = samples[samples.Length / 2];

        Assert.Equal(Saturation.TargetMedian, median * scale, 2);
    }

    [Fact]
    public void AMonochromeImageIsNeverColourised()
    {
        var greyscale = Enumerable.Repeat(0.01f, 500).ToArray();

        Assert.Equal(1f, Saturation.ScaleFromSamples(greyscale), 4);
    }

    [Fact]
    public void BoostBlendsTowardsNoScalingAtAll()
    {
        var muted = Enumerable.Repeat(0.15f, 500).ToArray();

        float full = Saturation.ScaleFromSamples(muted, 1.0);
        float half = Saturation.ScaleFromSamples(muted, 0.5);

        Assert.Equal(1f, Saturation.ScaleFromSamples(muted, 0.0), 4);
        Assert.Equal(1f + (full - 1f) * 0.5f, half, 3);
    }

    [Fact]
    public void ScalingIsNeverBelowOneOrAboveTheCap()
    {
        var rng = new Random(9);
        for (int trial = 0; trial < 50; trial++)
        {
            var samples = Enumerable.Range(0, 200).Select(_ => (float)rng.NextDouble()).ToArray();
            Assert.InRange(Saturation.ScaleFromSamples(samples), 1f, Saturation.MaxScale);
        }
    }

    [Fact]
    public void OnlyMidToneDataIsSampled()
    {
        // Saturation is meaningless at the extremes of lightness, so a picture that is all
        // near-black must report no usable samples rather than a wild scale.
        var pixels = Enumerable.Repeat(Argb(4, 0, 0), 400).ToArray();

        Assert.Empty(Saturation.SampleMidtones(new PixelGrid(pixels, 20, 20)));
    }

    // ------------------------------------------------------------------ colour space

    [Fact]
    public void HslRoundTripsBackToTheSameColour()
    {
        var rng = new Random(17);

        for (int i = 0; i < 500; i++)
        {
            int r = rng.Next(256), g = rng.Next(256), b = rng.Next(256);

            var (h, s, l) = ColorSpace.RgbToHsl(r / 255f, g / 255f, b / 255f);
            var (r2, g2, b2) = ColorSpace.HslToRgb(h, s, l);

            Assert.InRange(Math.Abs(r2 - r), 0, 1);
            Assert.InRange(Math.Abs(g2 - g), 0, 1);
            Assert.InRange(Math.Abs(b2 - b), 0, 1);
        }
    }

    [Fact]
    public void RaisingLightnessInHslDoesNotWashOutTheHue()
    {
        // The failure this replaced: scaling RGB channels and clamping at 255 turns a warm
        // mid tone towards white, which is how the output ended up looking grey.
        const float r0 = 0.78f, g0 = 0.59f, b0 = 0.59f;
        var (h, s, l) = ColorSpace.RgbToHsl(r0, g0, b0);

        // Through HSL: lightness up, saturation held.
        var (r, g, b) = ColorSpace.HslToRgb(h, s, 0.80f);
        var (_, viaHsl, _) = ColorSpace.RgbToHsl(r / 255f, g / 255f, b / 255f);

        // The old way: scale every channel by the same factor and clamp.
        float scale = 0.80f / l;
        var (_, viaScaling, _) = ColorSpace.RgbToHsl(
            Math.Clamp(r0 * scale, 0f, 1f),
            Math.Clamp(g0 * scale, 0f, 1f),
            Math.Clamp(b0 * scale, 0f, 1f));

        Assert.True(Math.Abs(viaHsl - s) < 0.02f,
            $"HSL should hold saturation at {s:F3}, got {viaHsl:F3}");

        // Channel scaling does not hold it. Which way it drifts depends on the colour, so
        // the claim is only that it drifts; either way the picture stops agreeing with
        // itself about how colourful each region is.
        Assert.True(Math.Abs(viaScaling - s) > 0.10f,
            $"channel scaling should have shifted saturation away from {s:F3}, got {viaScaling:F3}");
    }

    [Fact]
    public void ZeroSaturationGivesANeutralGrey()
    {
        var (r, g, b) = ColorSpace.HslToRgb(210f, 0f, 0.5f);

        Assert.Equal(r, g);
        Assert.Equal(g, b);
    }

    // ------------------------------------------------------------------ ramp calibration

    [Fact]
    public void CalibrationOrdersTheRampByMeasuredCoverage()
    {
        var glyphs = new GlyphSet();
        var (coverage, centroid) = FakeMeasurements(glyphs);

        glyphs.Calibrate(coverage, centroid);

        Assert.True(glyphs.IsCalibrated);

        var ramp = glyphs.RampDenseIndices;
        for (int i = 1; i < ramp.Length; i++)
            Assert.True(coverage[ramp[i]] >= coverage[ramp[i - 1]] - 1e-4f,
                $"ramp step {i} is lighter than step {i - 1}");
    }

    [Fact]
    public void TheCalibratedRampStartsBlankAndEndsDarkest()
    {
        var glyphs = new GlyphSet();
        var (coverage, centroid) = FakeMeasurements(glyphs);

        glyphs.Calibrate(coverage, centroid);

        Assert.Equal(GlyphSet.BlankIndex, glyphs.RampDenseIndices[0]);
        Assert.Equal(GlyphSet.BlankIndex, glyphs.RampIndices[0]);

        float darkest = glyphs.RampDenseIndices.Max(i => coverage[i]);
        Assert.Equal(darkest, coverage[glyphs.RampDenseIndices[^1]], 4);
    }

    [Fact]
    public void CalibrationPrefersGlyphsWhoseInkIsCentred()
    {
        var glyphs = new GlyphSet();
        var coverage = new float[glyphs.Count];
        var centroid = new float[glyphs.Count];

        // Two candidates of identical darkness, one centred and one hugging the bottom.
        int centred = glyphs.IndexOf('o');
        int lopsided = glyphs.IndexOf('_');

        for (int i = 0; i < coverage.Length; i++) { coverage[i] = 0f; centroid[i] = 0.5f; }
        coverage[centred] = 0.5f; centroid[centred] = 0.5f;
        coverage[lopsided] = 0.5f; centroid[lopsided] = 0.95f;

        glyphs.Calibrate(coverage, centroid);

        Assert.Equal(centred, glyphs.RampDenseIndices[^1]);
    }

    [Fact]
    public void CalibrationIsIgnoredWhenTheMeasurementsAreMissing()
    {
        var glyphs = new GlyphSet();
        var before = glyphs.RampDenseIndices;

        glyphs.Calibrate(new float[2], new float[2]);

        Assert.False(glyphs.IsCalibrated);
        Assert.Same(before, glyphs.RampDenseIndices);
    }

    /// <summary>Plausible coverage for each glyph, without needing a real font.</summary>
    private static (float[] Coverage, float[] CentroidY) FakeMeasurements(GlyphSet glyphs)
    {
        var coverage = new float[glyphs.Count];
        var centroid = new float[glyphs.Count];
        var rng = new Random(5);

        for (int i = 0; i < glyphs.Count; i++)
        {
            coverage[i] = glyphs.Chars[i] == ' ' ? 0f : (float)rng.NextDouble() * 0.5f;
            centroid[i] = 0.4f + (float)rng.NextDouble() * 0.2f;
        }

        return (coverage, centroid);
    }
}
