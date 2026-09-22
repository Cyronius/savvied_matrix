namespace SavviedMatrix.Render;

public enum GlyphMode
{
    /// <summary>Ten steps. Chunky, reads from a long way back.</summary>
    Ramp,

    /// <summary>Many steps. Much finer tonal gradation, still legible on a TV.</summary>
    Dense,

    /// <summary>Unicode shading blocks. The classic terminal-art look, very clean.</summary>
    Blocks,

    /// <summary>Half-width katakana, chosen at random per cell.</summary>
    Katakana,

    /// <summary>
    /// Not characters at all: each cell is split into a coloured top and bottom half, which
    /// doubles the vertical resolution and makes the effective pixels square. The most
    /// detail the grid can carry, at the cost of the picture being made of text.
    /// </summary>
    HalfBlock
}

/// <summary>
/// Every character the app can draw, flattened into one array so a cell only has to
/// carry a byte index. The atlas is built over this array once at startup, which is
/// what makes drawing a frame a memory copy instead of a few thousand text draws.
/// </summary>
public sealed class GlyphSet
{
    /// <summary>Ten steps, darkest first. Used until the atlas has measured the font.</summary>
    public const string Ramp = " .:-=+*#%@";

    /// <summary>
    /// The long-standing standard ASCII-art ramp, darkest first. Also a fallback: once the
    /// atlas exists, <see cref="Calibrate"/> replaces this with a ramp measured from the
    /// font actually in use.
    /// </summary>
    public const string RampDense =
        @" .'`^"",:;Il!i><~+_-?][}{1)(|\/tfjrxnuvczXYUJCLQ0OZmwqpdbkhao*#MW&8%B@$";

    /// <summary>Space, light, medium, dark shade, full block.</summary>
    public const string Blocks = " ░▒▓█";

    /// <summary>
    /// Half-width katakana plus digits. Half-width so the glyphs fit a narrow cell;
    /// these are the characters the film's rain is built from.
    /// </summary>
    public const string Katakana =
        "ﾊﾐﾋｰｳｼﾅﾒﾆｻﾜﾂ" +
        "ｵﾘｱﾎﾃﾏｹｴｶｷﾑ" +
        "ﾕﾗｾﾈｽﾀﾇﾍ" +
        "0123456789Z";

    /// <summary>Steps in the calibrated fine ramp.</summary>
    public const int DenseSteps = 48;

    /// <summary>
    /// How much an off-centre glyph is penalised when picking a ramp step, in units of
    /// coverage. Small, so it only breaks ties between glyphs of similar darkness, but
    /// enough to prefer a centred character over an underscore or a quotation mark, which
    /// tile into visible lines along the bottom or the top of the picture.
    /// </summary>
    public const double CentrePenalty = 0.06;

    private readonly Dictionary<char, int> _index;
    private readonly int[] _asciiIndices;

    public char[] Chars { get; }

    public int[] RampIndices { get; private set; }
    public int[] RampDenseIndices { get; private set; }
    public int[] BlockIndices { get; }
    public int[] KatakanaIndices { get; }

    /// <summary>Characters used for falling stream heads and trails.</summary>
    public int[] StreamIndices { get; }

    /// <summary>True once the ramps have been rebuilt from measured glyph coverage.</summary>
    public bool IsCalibrated { get; private set; }

    public const byte BlankIndex = 0;

    public int Count => Chars.Length;

    public GlyphSet()
    {
        var chars = new List<char> { ' ' };
        _index = new Dictionary<char, int> { [' '] = 0 };

        int Intern(char c)
        {
            if (_index.TryGetValue(c, out int existing)) return existing;
            int i = chars.Count;
            chars.Add(c);
            _index[c] = i;
            return i;
        }

        RampIndices = Ramp.Select(Intern).ToArray();
        RampDenseIndices = RampDense.Select(Intern).ToArray();
        BlockIndices = Blocks.Select(Intern).ToArray();
        KatakanaIndices = Katakana.Distinct().Select(Intern).ToArray();

        // The rain streams always use katakana regardless of the image glyph mode:
        // a ramp of punctuation falling down the screen does not read as the Matrix.
        StreamIndices = KatakanaIndices;

        // Printable ASCII, so the app can put a readable status line on the screen, and so
        // the calibrated ramps have a full alphabet of densities to choose from.
        var ascii = new List<int>();
        for (char c = ' '; c <= '~'; c++) ascii.Add(Intern(c));
        _asciiIndices = ascii.ToArray();

        Chars = chars.ToArray();
    }

    /// <summary>
    /// Rebuilds the two ASCII ramps from the coverage the atlas measured, so that a step
    /// up in brightness is a step up in actual ink. The conventional ramps are ordered by
    /// eye and by a different font; on a ten pixel cell in Consolas several of their steps
    /// are indistinguishable while others jump, which shows up as banding in a photograph.
    /// </summary>
    public void Calibrate(float[] coverage, float[] centroidY)
    {
        if (coverage.Length < Count || centroidY.Length < Count) return;

        RampIndices = BuildRamp(coverage, centroidY, Ramp.Length);
        RampDenseIndices = BuildRamp(coverage, centroidY, DenseSteps);
        IsCalibrated = true;
    }

    private int[] BuildRamp(float[] coverage, float[] centroidY, int steps)
    {
        float max = 0f;
        foreach (int i in _asciiIndices) max = Math.Max(max, coverage[i]);

        if (max <= 0f || steps < 2) return RampIndices;

        var ramp = new int[steps];

        for (int s = 0; s < steps; s++)
        {
            float target = max * s / (steps - 1);

            int best = BlankIndex;
            double bestScore = double.MaxValue;

            foreach (int i in _asciiIndices)
            {
                double score = Math.Abs(coverage[i] - target)
                             + CentrePenalty * Math.Abs(centroidY[i] - 0.5);

                if (score < bestScore)
                {
                    bestScore = score;
                    best = i;
                }
            }

            ramp[s] = best;
        }

        // The darkest step is always an empty cell, whatever the measurement says.
        ramp[0] = BlankIndex;

        return ramp;
    }

    /// <summary>The brightness ramp for a mode, darkest first. Katakana has none.</summary>
    public int[] RampFor(GlyphMode mode) => mode switch
    {
        GlyphMode.Dense => RampDenseIndices,
        GlyphMode.Blocks => BlockIndices,
        _ => RampIndices
    };

    /// <summary>Glyph index for a character, or the blank cell when it is not in the set.</summary>
    public byte IndexOf(char c) => _index.TryGetValue(c, out int i) ? (byte)i : BlankIndex;

    public static GlyphMode ParseMode(string? value) => value?.ToLowerInvariant() switch
    {
        "katakana" => GlyphMode.Katakana,
        "blocks" => GlyphMode.Blocks,
        "half" or "halfblock" or "half-block" => GlyphMode.HalfBlock,
        "ramp" => GlyphMode.Ramp,
        _ => GlyphMode.Dense
    };
}
