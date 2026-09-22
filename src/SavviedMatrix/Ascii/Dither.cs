namespace SavviedMatrix.Ascii;

/// <summary>
/// Ordered (Bayer) dithering for the colour quantiser.
///
/// Sixteen colours with hard thresholds turn a smooth gradient into flat bands with visible
/// edges, which is what makes a quantised photograph look like a cheap GIF rather than like
/// terminal art. Nudging each cell's brightness by a fixed, position-dependent amount before
/// the threshold turns those edges into a stipple, and the eye reads the mixture as an
/// intermediate colour.
///
/// Ordered rather than error-diffused on purpose: the pattern depends only on the cell's
/// coordinates, so it is stable frame to frame. Error diffusion would crawl as the rain
/// swept over the picture.
/// </summary>
public static class Dither
{
    /// <summary>The classic 8x8 Bayer threshold matrix.</summary>
    private static readonly int[] Bayer8 =
    {
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21
    };

    public const int Size = 8;

    /// <summary>
    /// Brightness offset for a cell, in [-strength/2, +strength/2). Zero strength gives
    /// exactly zero, so dithering can be switched off without changing anything else.
    /// </summary>
    public static float Offset(int x, int y, double strength)
    {
        if (strength <= 0) return 0f;

        int ix = ((x % Size) + Size) % Size;
        int iy = ((y % Size) + Size) % Size;

        // The half-step is what centres the pattern on zero. Without it the matrix runs
        // from -0.5 to +0.484 and every image comes out a fraction darker than it should.
        float unit = (Bayer8[iy * Size + ix] + 0.5f) / 64f - 0.5f;

        return unit * (float)strength;
    }
}
