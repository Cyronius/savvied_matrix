namespace SavviedMatrix.Ascii;

/// <summary>
/// RGB and HSL conversion.
///
/// The colour pipeline works in HSL rather than scaling RGB channels directly, because
/// scaling channels and then clamping at 255 quietly desaturates: a warm mid tone pushed
/// brighter loses its red channel to the ceiling first and drifts towards white. Adjusting
/// lightness with the hue and saturation held is the whole point.
/// </summary>
public static class ColorSpace
{
    /// <summary>RGB in [0,1] to hue in degrees [0,360), saturation and lightness in [0,1].</summary>
    public static (float Hue, float Saturation, float Lightness) RgbToHsl(float r, float g, float b)
    {
        float max = Math.Max(r, Math.Max(g, b));
        float min = Math.Min(r, Math.Min(g, b));
        float lightness = (max + min) / 2f;
        float delta = max - min;

        if (delta < 1e-6f) return (0f, 0f, lightness);

        float saturation = lightness > 0.5f
            ? delta / (2f - max - min)
            : delta / (max + min);

        float hue;
        if (max == r) hue = ((g - b) / delta + (g < b ? 6f : 0f)) * 60f;
        else if (max == g) hue = ((b - r) / delta + 2f) * 60f;
        else hue = ((r - g) / delta + 4f) * 60f;

        if (hue >= 360f) hue -= 360f;
        if (hue < 0f) hue += 360f;

        return (hue, saturation, lightness);
    }

    /// <summary>Hue in degrees, saturation and lightness in [0,1], to 8-bit RGB.</summary>
    public static (int R, int G, int B) HslToRgb(float hue, float saturation, float lightness)
    {
        lightness = Math.Clamp(lightness, 0f, 1f);
        saturation = Math.Clamp(saturation, 0f, 1f);

        if (saturation <= 1e-6f)
        {
            int grey = Round(lightness);
            return (grey, grey, grey);
        }

        hue = hue % 360f;
        if (hue < 0f) hue += 360f;

        float c = (1f - Math.Abs(2f * lightness - 1f)) * saturation;
        float section = hue / 60f;
        float x = c * (1f - Math.Abs(section % 2f - 1f));

        (float r, float g, float b) = section switch
        {
            < 1f => (c, x, 0f),
            < 2f => (x, c, 0f),
            < 3f => (0f, c, x),
            < 4f => (0f, x, c),
            < 5f => (x, 0f, c),
            _ => (c, 0f, x)
        };

        float m = lightness - c / 2f;
        return (Round(r + m), Round(g + m), Round(b + m));
    }

    private static int Round(float v) => (int)Math.Clamp(MathF.Round(v * 255f), 0f, 255f);
}
