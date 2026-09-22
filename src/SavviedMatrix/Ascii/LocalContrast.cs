namespace SavviedMatrix.Ascii;

/// <summary>
/// Local tone mapping over the character grid.
///
/// A global contrast stretch only guarantees that the whole image spans the range. In a
/// photograph with a bright sky and a dark foreground the sky takes nearly all of it and
/// the foreground collapses into two or three characters, which is why photographs are so
/// much harder to read as text art than graphics are. Expanding each cell away from its
/// local average instead gives every region its own share of the ramp.
///
/// This is one pass over ten thousand floats on a background thread, so it is free in
/// practice. The blur is a sliding-window box blur, run twice to approximate a Gaussian.
/// </summary>
public static class LocalContrast
{
    /// <summary>Neighbourhood radius as a fraction of the grid's shorter side.</summary>
    public const double RadiusFraction = 1.0 / 6.0;

    public const int MinRadius = 2;

    /// <summary>
    /// Returns a copy of <paramref name="values"/> with local contrast expanded by
    /// <paramref name="amount"/>. Zero returns the input unchanged.
    /// </summary>
    public static float[] Apply(float[] values, int width, int height, double amount)
    {
        if (amount <= 0 || values.Length == 0 || width <= 0 || height <= 0)
            return values;

        int radius = Math.Max(MinRadius, (int)(Math.Min(width, height) * RadiusFraction));

        var blurred = Blur(values, width, height, radius);
        var result = new float[values.Length];

        float gain = (float)amount;

        for (int i = 0; i < values.Length; i++)
        {
            float detail = values[i] - blurred[i];
            result[i] = Math.Clamp(values[i] + gain * detail, 0f, 1f);
        }

        return result;
    }

    /// <summary>Two passes of a separable box blur, which is close enough to a Gaussian here.</summary>
    public static float[] Blur(float[] values, int width, int height, int radius)
    {
        var a = new float[values.Length];
        var b = new float[values.Length];

        Array.Copy(values, a, values.Length);

        for (int pass = 0; pass < 2; pass++)
        {
            BlurHorizontal(a, b, width, height, radius);
            BlurVertical(b, a, width, height, radius);
        }

        return a;
    }

    private static void BlurHorizontal(float[] src, float[] dst, int width, int height, int radius)
    {
        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            double sum = 0;
            int count = 0;

            // Prime the window at x = 0.
            for (int x = 0; x <= Math.Min(radius, width - 1); x++)
            {
                sum += src[row + x];
                count++;
            }

            for (int x = 0; x < width; x++)
            {
                dst[row + x] = (float)(sum / count);

                int outgoing = x - radius;
                int incoming = x + radius + 1;

                if (outgoing >= 0)
                {
                    sum -= src[row + outgoing];
                    count--;
                }
                if (incoming < width)
                {
                    sum += src[row + incoming];
                    count++;
                }
            }
        }
    }

    private static void BlurVertical(float[] src, float[] dst, int width, int height, int radius)
    {
        for (int x = 0; x < width; x++)
        {
            double sum = 0;
            int count = 0;

            for (int y = 0; y <= Math.Min(radius, height - 1); y++)
            {
                sum += src[y * width + x];
                count++;
            }

            for (int y = 0; y < height; y++)
            {
                dst[y * width + x] = (float)(sum / count);

                int outgoing = y - radius;
                int incoming = y + radius + 1;

                if (outgoing >= 0)
                {
                    sum -= src[outgoing * width + x];
                    count--;
                }
                if (incoming < height)
                {
                    sum += src[incoming * width + x];
                    count++;
                }
            }
        }
    }
}
