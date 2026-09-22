using SavviedMatrix.Core;

namespace SavviedMatrix.Ascii;

/// <summary>
/// Per-image saturation normalisation.
///
/// Quantising to sixteen colours punishes muted images. Four of the sixteen ANSI slots are
/// neutral, and a colour has to be fairly vivid before it is closer to a hue than to a
/// grey, so an ordinary photograph comes out looking monochrome even though the original
/// was not. Stretching each image's own saturation before matching is the same trick
/// already applied to brightness: judge the picture by its own distribution.
///
/// The statistic is the median rather than a high percentile, because the problem is the
/// bulk of the image, not its most colourful corner. A few vivid pixels should not veto
/// the boost for everything else.
/// </summary>
public static class Saturation
{
    /// <summary>Where a typical cell is aimed, comfortably past the point where hue wins.</summary>
    public const float TargetMedian = 0.55f;

    /// <summary>
    /// An image whose colour never gets past this is taken to be genuinely monochrome and
    /// left alone, so a black and white photograph is not colourised.
    /// </summary>
    public const float MonochromeThreshold = 0.08f;

    public const float MaxScale = 6f;

    /// <summary>
    /// Saturation is unstable and meaningless at the extremes of lightness, so only
    /// mid-tone pixels are sampled when deciding how colourful an image is.
    /// </summary>
    public const float MinSampleLightness = 0.12f;
    public const float MaxSampleLightness = 0.90f;

    /// <summary>Saturation of the mid-tone pixels, which is what the scale is judged from.</summary>
    public static float[] SampleMidtones(PixelGrid image)
    {
        var samples = new List<float>(image.Argb.Length / 2);

        for (int i = 0; i < image.Width * image.Height; i++)
        {
            var (r, g, b) = PixelGrid.Split(image.Argb[i]);
            var (_, saturation, lightness) = ColorSpace.RgbToHsl(r / 255f, g / 255f, b / 255f);

            if (lightness >= MinSampleLightness && lightness <= MaxSampleLightness)
                samples.Add(saturation);
        }

        return samples.ToArray();
    }

    /// <summary>
    /// Multiplier that lifts the median of <paramref name="samples"/> to
    /// <see cref="TargetMedian"/>. Never below 1: an already vivid image is left alone
    /// rather than washed out. <paramref name="boost"/> blends towards 1, so 0 disables it.
    /// </summary>
    public static float ScaleFromSamples(float[] samples, double boost = 1.0)
    {
        if (samples.Length == 0 || boost <= 0) return 1f;

        var sorted = (float[])samples.Clone();
        Array.Sort(sorted);

        float median = Luminance.Percentile(sorted, 0.5);
        float high = Luminance.Percentile(sorted, 0.9);

        // Nothing anywhere in the picture has real colour: leave it as it is.
        if (high < MonochromeThreshold) return 1f;

        if (median < 1e-4f) median = Math.Max(high * 0.5f, 1e-4f);

        float scale = Math.Clamp(TargetMedian / median, 1f, MaxScale);

        return 1f + (scale - 1f) * (float)Math.Clamp(boost, 0.0, 1.0);
    }

    public static float ScaleFor(PixelGrid image, double boost = 1.0)
        => ScaleFromSamples(SampleMidtones(image), boost);
}
