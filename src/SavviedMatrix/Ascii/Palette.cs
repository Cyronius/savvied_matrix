using System.Drawing;

namespace SavviedMatrix.Ascii;

public enum PaletteMode
{
    /// <summary>Sixteen shades of green. Brightness only; the image's own hues are discarded.</summary>
    Matrix,

    /// <summary>The classic sixteen ANSI terminal colours, matched to the image's actual hues.</summary>
    Ansi
}

/// <summary>
/// Sixteen colour slots plus one highlight, indexed by a byte in each cell.
///
/// Sixteen is not an arbitrary limit: it is what an ANSI terminal has, and quantising to it
/// is what makes the output read as terminal art rather than as a photograph with a green
/// filter over it. In Matrix mode the sixteen are a brightness ramp; in ANSI mode they are
/// hues, and brightness is carried entirely by the choice of character.
/// </summary>
public sealed class Palette
{
    public const int Count = 17;
    public const byte HighlightIndex = 16;
    public const int ShadeCount = 16;

    public PaletteMode Mode { get; }
    public Color[] Colors { get; }

    public static readonly Color Background = Color.Black;

    // Sixteen green shades. Slot 0 is not black: dark parts of the picture still need to
    // read as lit characters, or the image dissolves into the background instead of
    // looking like it is rendered in text.
    private static readonly Color MatrixDarkest = Color.FromArgb(0x00, 0x2A, 0x00);
    private static readonly Color MatrixMid = Color.FromArgb(0x00, 0x8F, 0x11);
    private static readonly Color MatrixBrightest = Color.FromArgb(0x00, 0xFF, 0x41);
    private static readonly Color MatrixHighlight = Color.FromArgb(0xB4, 0xFF, 0xC8);

    /// <summary>The standard sixteen, in the conventional ANSI order.</summary>
    private static readonly Color[] Ansi16 =
    {
        Color.FromArgb(0x00, 0x00, 0x00), // 0  black
        Color.FromArgb(0xAA, 0x00, 0x00), // 1  red
        Color.FromArgb(0x00, 0xAA, 0x00), // 2  green
        Color.FromArgb(0xAA, 0x55, 0x00), // 3  yellow (brown)
        Color.FromArgb(0x00, 0x00, 0xAA), // 4  blue
        Color.FromArgb(0xAA, 0x00, 0xAA), // 5  magenta
        Color.FromArgb(0x00, 0xAA, 0xAA), // 6  cyan
        Color.FromArgb(0xAA, 0xAA, 0xAA), // 7  light grey
        Color.FromArgb(0x55, 0x55, 0x55), // 8  dark grey
        Color.FromArgb(0xFF, 0x55, 0x55), // 9  bright red
        Color.FromArgb(0x55, 0xFF, 0x55), // 10 bright green
        Color.FromArgb(0xFF, 0xFF, 0x55), // 11 bright yellow
        Color.FromArgb(0x55, 0x55, 0xFF), // 12 bright blue
        Color.FromArgb(0xFF, 0x55, 0xFF), // 13 bright magenta
        Color.FromArgb(0x55, 0xFF, 0xFF), // 14 bright cyan
        Color.FromArgb(0xFF, 0xFF, 0xFF)  // 15 white
    };

    /// <summary>
    /// The green fade a rain trail runs through, brightest first. In ANSI mode the sixteen
    /// slots are hues rather than a ramp, so the trail needs its own explicit path through
    /// them or it would cycle through unrelated colours as it faded.
    /// </summary>
    private static readonly byte[] AnsiTrail = { HighlightIndex, 15, 10, 10, 2, 2, 8, 0 };

    public Palette(PaletteMode mode)
    {
        Mode = mode;
        Colors = new Color[Count];

        if (mode == PaletteMode.Ansi)
        {
            Array.Copy(Ansi16, Colors, ShadeCount);
            Colors[HighlightIndex] = Color.White;
        }
        else
        {
            for (int i = 0; i < ShadeCount; i++) Colors[i] = MatrixShade(i);
            Colors[HighlightIndex] = MatrixHighlight;
        }
    }

    public static PaletteMode ParseMode(string? value) =>
        string.Equals(value, "matrix", StringComparison.OrdinalIgnoreCase)
            ? PaletteMode.Matrix
            : PaletteMode.Ansi;

    /// <summary>Colour for an index, clamped into range.</summary>
    public Color this[int index] => Colors[Math.Clamp(index, 0, Count - 1)];

    /// <summary>Saturation at or above which a cell is given a hue rather than a grey.</summary>
    public const float ChromaThreshold = 0.22f;

    /// <summary>Below this lightness a cell is black whatever colour it started as.</summary>
    public const float BlackThreshold = 0.10f;

    /// <summary>Lightness at which a hue switches from its dim slot to its bright one.</summary>
    public const float BrightThreshold = 0.45f;

    /// <summary>The six ANSI hues at 60 degree centres: red, yellow, green, cyan, blue, magenta.</summary>
    private static readonly byte[] DimHues = { 1, 3, 2, 6, 4, 5 };
    private static readonly byte[] BrightHues = { 9, 11, 10, 14, 12, 13 };

    /// <summary>
    /// The slot for a cell. In Matrix mode this is brightness alone. In ANSI mode a cell
    /// below the chroma threshold takes a neutral, one below the black threshold is black,
    /// and anything else takes the dim or bright variant of its nearest hue.
    /// </summary>
    public byte IndexFor(int argb, float stretchedLuminance, float saturationScale = 1f, float dither = 0f)
    {
        if (Mode == PaletteMode.Matrix) return ShadeIndex(stretchedLuminance + dither);

        var (r, g, b) = Core.PixelGrid.Split(argb);

        // Work in HSL. Scaling the RGB channels directly and clamping at 255 quietly
        // desaturates, because a warm tone pushed brighter loses its red channel to the
        // ceiling first and drifts towards white.
        var (hue, saturation, _) = ColorSpace.RgbToHsl(r / 255f, g / 255f, b / 255f);

        // The dither only moves where a threshold falls, never the brightness the
        // character already drew.
        saturation = Math.Clamp(saturation * saturationScale + dither, 0f, 1f);
        float lightness = Math.Clamp(stretchedLuminance + dither, 0f, 1f);

        if (lightness < BlackThreshold) return 0;

        // Choose the hue deliberately rather than by nearest colour in RGB. Nearest-colour
        // matching sends anything under roughly 0.4 saturation to a grey, and the bulk of
        // an ordinary photograph sits below that, so the whole picture came out neutral.
        // Brightness is already carried by the choice of character, which frees the sixteen
        // slots to carry hue.
        if (saturation < ChromaThreshold) return ShadeIndex(lightness);

        int sector = (int)MathF.Round(hue / 60f) % 6;
        if (sector < 0) sector += 6;

        return lightness >= BrightThreshold ? BrightHues[sector] : DimHues[sector];
    }

    /// <summary>Pure brightness ramp over the sixteen slots, used for text and Matrix mode.</summary>
    public byte ShadeIndex(float luminance)
    {
        if (Mode == PaletteMode.Ansi)
        {
            // A greyscale run through the ANSI slots that are actually grey.
            float l = Math.Clamp(luminance, 0f, 1f);
            if (l < 0.2f) return 0;
            if (l < 0.45f) return 8;
            if (l < 0.75f) return 7;
            return 15;
        }

        int shade = (int)Math.Floor(Math.Clamp(luminance, 0f, 1f) * (ShadeCount - 1));
        return (byte)Math.Clamp(shade, 0, ShadeCount - 1);
    }

    /// <summary>
    /// Colour for a rain trail cell. <paramref name="fade"/> is 1 at the head and 0 at the
    /// far end of the trail.
    /// </summary>
    public byte TrailIndex(double fade)
    {
        fade = Math.Clamp(fade, 0.0, 1.0);

        if (Mode == PaletteMode.Ansi)
        {
            int i = (int)Math.Round((1.0 - fade) * (AnsiTrail.Length - 1));
            return AnsiTrail[Math.Clamp(i, 0, AnsiTrail.Length - 1)];
        }

        if (fade >= 0.999) return HighlightIndex;
        return (byte)Math.Clamp((int)Math.Round(fade * (ShadeCount - 1)), 0, ShadeCount - 1);
    }

    /// <summary>Nearest of the sixteen ANSI colours, by perceptually weighted distance.</summary>
    public static byte NearestAnsi(int r, int g, int b)
    {
        double best = double.MaxValue;
        byte bestIndex = 0;

        for (byte i = 0; i < Ansi16.Length; i++)
        {
            var c = Ansi16[i];
            double dr = r - c.R;
            double dg = g - c.G;
            double db = b - c.B;

            // Weights approximate how much each channel contributes to perceived
            // difference; unweighted RGB distance sends too many mid tones to blue.
            double distance = 2.0 * dr * dr + 4.0 * dg * dg + 3.0 * db * db;

            if (distance < best)
            {
                best = distance;
                bestIndex = i;
            }
        }

        return bestIndex;
    }

    private static Color MatrixShade(int index)
    {
        const int mid = 8;
        const int top = ShadeCount - 1;

        if (index <= 0) return MatrixDarkest;
        if (index >= top) return MatrixBrightest;

        return index <= mid
            ? Lerp(MatrixDarkest, MatrixMid, (float)index / mid)
            : Lerp(MatrixMid, MatrixBrightest, (float)(index - mid) / (top - mid));
    }

    private static Color Lerp(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)Math.Round(a.R + (b.R - a.R) * t),
            (int)Math.Round(a.G + (b.G - a.G) * t),
            (int)Math.Round(a.B + (b.B - a.B) * t));
    }
}
