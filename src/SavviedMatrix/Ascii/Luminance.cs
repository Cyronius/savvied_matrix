using SavviedMatrix.Core;

namespace SavviedMatrix.Ascii;

/// <summary>
/// Turns pixels into per-cell brightness in [0,1].
/// The contrast stretch matters more than it sounds: a ten-step character ramp has so
/// little dynamic range that an unstretched photograph collapses into two or three
/// glyphs and reads as mush.
/// </summary>
public static class Luminance
{
    public const double LowPercentile = 0.02;
    public const double HighPercentile = 0.98;

    /// <summary>Rec. 709 relative luminance of each pixel, in [0,1], row-major.</summary>
    public static float[] Compute(PixelGrid image)
    {
        var result = new float[image.Width * image.Height];
        for (int i = 0; i < result.Length; i++)
        {
            var (r, g, b) = PixelGrid.Split(image.Argb[i]);
            result[i] = (0.2126f * r + 0.7152f * g + 0.0722f * b) / 255f;
        }
        return result;
    }

    /// <summary>
    /// Maps the 2nd percentile to 0 and the 98th to 1, clamping the tails, then applies
    /// gamma. A flat image, where the two percentiles coincide, becomes a uniform 0.5
    /// rather than a division by zero.
    /// </summary>
    public static float[] ContrastStretch(float[] values, double gamma = 1.0)
    {
        var result = new float[values.Length];
        if (values.Length == 0) return result;

        var sorted = (float[])values.Clone();
        Array.Sort(sorted);

        float low = Percentile(sorted, LowPercentile);
        float high = Percentile(sorted, HighPercentile);
        float range = high - low;

        bool applyGamma = Math.Abs(gamma - 1.0) > 1e-6;

        if (range < 1e-6f)
        {
            float flat = applyGamma ? (float)Math.Pow(0.5, gamma) : 0.5f;
            Array.Fill(result, flat);
            return result;
        }

        for (int i = 0; i < values.Length; i++)
        {
            float v = (values[i] - low) / range;
            v = Math.Clamp(v, 0f, 1f);
            if (applyGamma) v = (float)Math.Pow(v, gamma);
            result[i] = v;
        }

        return result;
    }

    /// <summary>
    /// Histogram equalisation, blended with the input by <paramref name="strength"/>.
    ///
    /// A linear stretch only guarantees the image spans the range; it does nothing about
    /// how the values are distributed inside it. A photograph that is two thirds dark
    /// background spends two thirds of its cells on the bottom few characters and the
    /// subject gets whatever is left. With roughly thirty usable glyph levels that is the
    /// difference between a picture and a silhouette, so redistributing the levels so each
    /// carries a similar number of cells is close to the correct transform here rather
    /// than a cosmetic one.
    ///
    /// Full strength can look processed and can amplify noise in flat areas, hence the blend.
    /// </summary>
    public static float[] Equalize(float[] values, double strength, int bins = 256)
    {
        var result = new float[values.Length];
        if (values.Length == 0) return result;

        if (strength <= 0)
        {
            Array.Copy(values, result, values.Length);
            return result;
        }

        bins = Math.Clamp(bins, 2, 4096);

        var histogram = new int[bins];
        foreach (var v in values)
        {
            int bin = (int)(Math.Clamp(v, 0f, 1f) * (bins - 1));
            histogram[bin]++;
        }

        // Cumulative distribution, normalised so the darkest occupied bin maps to 0.
        var cdf = new float[bins];
        int running = 0;
        int first = -1;

        for (int i = 0; i < bins; i++)
        {
            running += histogram[i];
            cdf[i] = running;
            if (first < 0 && histogram[i] > 0) first = i;
        }

        if (first < 0) return result;

        float low = cdf[first];
        float span = values.Length - low;

        if (span <= 0)
        {
            Array.Fill(result, 0.5f);
            return result;
        }

        for (int i = 0; i < bins; i++)
            cdf[i] = Math.Clamp((cdf[i] - low) / span, 0f, 1f);

        float blend = (float)Math.Clamp(strength, 0.0, 1.0);

        for (int i = 0; i < values.Length; i++)
        {
            float v = Math.Clamp(values[i], 0f, 1f);
            int bin = (int)(v * (bins - 1));
            result[i] = v + (cdf[bin] - v) * blend;
        }

        return result;
    }

    /// <summary>Nearest-rank percentile of an already sorted array.</summary>
    public static float Percentile(float[] sorted, double p)
    {
        if (sorted.Length == 0) return 0f;
        int index = (int)Math.Round(p * (sorted.Length - 1), MidpointRounding.AwayFromZero);
        return sorted[Math.Clamp(index, 0, sorted.Length - 1)];
    }
}
