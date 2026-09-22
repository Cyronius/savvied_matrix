using SavviedMatrix.Core;

namespace SavviedMatrix.Audio;

/// <summary>
/// Mean colour of the image under one vertical band of the screen.
/// Coverage is the fraction of the band's columns that fall inside the image
/// (as opposed to letterbox); a band below the coverage threshold is a musical rest.
/// </summary>
public readonly record struct BandStats(float Hue, float Saturation, float Luminance, float Coverage)
{
    public const float RestCoverageThreshold = 0.10f;
    public const float GreyscaleSaturationThreshold = 0.15f;

    public bool IsRest => Coverage < RestCoverageThreshold;
    public bool IsGreyscale => Saturation < GreyscaleSaturationThreshold;
}

public static class BandAnalyzer
{
    /// <summary>
    /// Splits the screen's columns into <paramref name="bands"/> equal vertical bands and
    /// averages the image colour under each. <paramref name="image"/> is the already
    /// downsampled grid whose pixel (x, y) corresponds to screen cell
    /// (placement.OffsetCol + x, placement.OffsetRow + y).
    /// </summary>
    public static BandStats[] Analyze(PixelGrid image, GridPlacement placement, int screenCols, int bands)
    {
        if (bands <= 0) throw new ArgumentOutOfRangeException(nameof(bands));

        var result = new BandStats[bands];

        for (int b = 0; b < bands; b++)
        {
            // Half-open column range for this band, spread evenly across the screen.
            int startCol = (int)((long)b * screenCols / bands);
            int endCol = (int)((long)(b + 1) * screenCols / bands);
            if (endCol <= startCol) endCol = startCol + 1;

            double sumR = 0, sumG = 0, sumB = 0;
            long samples = 0;
            int coveredCols = 0;
            int totalCols = endCol - startCol;

            for (int col = startCol; col < endCol; col++)
            {
                int imgX = col - placement.OffsetCol;
                if (imgX < 0 || imgX >= placement.FitCols || imgX >= image.Width)
                    continue;

                coveredCols++;

                int rowLimit = Math.Min(placement.FitRows, image.Height);
                for (int y = 0; y < rowLimit; y++)
                {
                    var (r, g, bl) = PixelGrid.Split(image[imgX, y]);
                    sumR += r;
                    sumG += g;
                    sumB += bl;
                    samples++;
                }
            }

            float coverage = totalCols == 0 ? 0f : (float)coveredCols / totalCols;

            if (samples == 0)
            {
                result[b] = new BandStats(0f, 0f, 0f, coverage);
                continue;
            }

            float r8 = (float)(sumR / samples / 255.0);
            float g8 = (float)(sumG / samples / 255.0);
            float b8 = (float)(sumB / samples / 255.0);

            var (hue, sat, lum) = RgbToHsl(r8, g8, b8);
            result[b] = new BandStats(hue, sat, lum, coverage);
        }

        return result;
    }

    /// <summary>RGB in [0,1] to (hue degrees [0,360), saturation [0,1], luminance [0,1]).</summary>
    public static (float Hue, float Saturation, float Luminance) RgbToHsl(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float lum = (max + min) / 2f;
        float delta = max - min;

        if (delta < 1e-6f)
            return (0f, 0f, lum);

        float sat = lum > 0.5f
            ? delta / (2f - max - min)
            : delta / (max + min);

        float hue;
        if (max == r) hue = ((g - b) / delta + (g < b ? 6f : 0f)) * 60f;
        else if (max == g) hue = ((b - r) / delta + 2f) * 60f;
        else hue = ((r - g) / delta + 4f) * 60f;

        if (hue >= 360f) hue -= 360f;
        if (hue < 0f) hue += 360f;

        return (hue, sat, lum);
    }
}
