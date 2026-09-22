using SavviedMatrix.Ascii;
using SavviedMatrix.Core;
using SavviedMatrix.Render;

namespace SavviedMatrix.Rain;

/// <summary>
/// Turns a rain field into the glyph and colour arrays the compositor draws.
///
/// One pass carries the whole transition: ahead of the falling head the outgoing image is
/// still standing, the head and its trail are the rain itself, and behind the trail the
/// incoming image has settled. The screen is never blank, so images hand over to each
/// other in a single continuous motion rather than dissolving to black and starting again.
///
/// Buffers are allocated once and rewritten in place: a rain frame must not allocate, or
/// the garbage collector will show up as stutter on a slow machine.
/// </summary>
public sealed class RainCompositor
{
    /// <summary>Chance per frame that a trail character changes, giving the flickering look.</summary>
    public const double GlyphChurnProbability = 0.10;

    private readonly int _cols;
    private readonly int _rows;
    private readonly GlyphSet _glyphs;
    private readonly Palette _palette;
    private readonly Random _rng;

    private readonly byte[] _glyphBuffer;
    private readonly byte[] _colourBuffer;
    private readonly byte[] _lowerBuffer;
    private readonly byte[] _streamGlyph;

    public byte[] Glyphs => _glyphBuffer;
    public byte[] Colours => _colourBuffer;
    public byte[] Lowers => _lowerBuffer;

    public RainCompositor(GridGeometry geometry, GlyphSet glyphs, Palette palette, Random rng)
    {
        _cols = geometry.Cols;
        _rows = geometry.Rows;
        _glyphs = glyphs;
        _palette = palette;
        _rng = rng;

        int count = geometry.CellCount;
        _glyphBuffer = new byte[count];
        _colourBuffer = new byte[count];
        _lowerBuffer = new byte[count];
        _streamGlyph = new byte[count];

        Array.Fill(_lowerBuffer, CellGrid.NotHalfBlock);

        ReseedStreamGlyphs();
    }

    /// <summary>Gives every cell a fresh random stream character. Called when a phase starts.</summary>
    public void ReseedStreamGlyphs()
    {
        var stream = _glyphs.StreamIndices;
        for (int i = 0; i < _streamGlyph.Length; i++)
            _streamGlyph[i] = (byte)stream[_rng.Next(stream.Length)];
    }

    /// <summary>
    /// Writes the transition frame at phase time <paramref name="t"/> into the internal
    /// buffers. <paramref name="outgoing"/> is what the rain is carrying away and
    /// <paramref name="incoming"/> is what it leaves behind.
    /// </summary>
    public void Evaluate(StreamParams[] streams, double t, CellGrid outgoing, CellGrid incoming)
    {
        var streamGlyphs = _glyphs.StreamIndices;

        for (int col = 0; col < _cols; col++)
        {
            ref readonly var stream = ref streams[col];

            double head = stream.HeadAt(t);
            bool entered = head >= 0;
            int headRow = (int)Math.Floor(head);
            int trail = Math.Max(1, stream.TrailLength);

            for (int row = 0; row < _rows; row++)
            {
                int i = row * _cols + col;

                if (!entered || row > headRow)
                {
                    // The rain has not arrived: the old image is still standing here.
                    _glyphBuffer[i] = outgoing.Glyph[i];
                    _colourBuffer[i] = outgoing.Colour[i];
                    _lowerBuffer[i] = outgoing.Lower[i];
                    continue;
                }

                if (row == headRow)
                {
                    if (_rng.NextDouble() < GlyphChurnProbability)
                        _streamGlyph[i] = (byte)streamGlyphs[_rng.Next(streamGlyphs.Length)];

                    _glyphBuffer[i] = _streamGlyph[i];
                    _colourBuffer[i] = Palette.HighlightIndex;
                    _lowerBuffer[i] = CellGrid.NotHalfBlock;
                    continue;
                }

                int distance = headRow - row;

                if (distance < trail)
                {
                    if (_rng.NextDouble() < GlyphChurnProbability)
                        _streamGlyph[i] = (byte)streamGlyphs[_rng.Next(streamGlyphs.Length)];

                    _glyphBuffer[i] = _streamGlyph[i];
                    _colourBuffer[i] = _palette.TrailIndex(1.0 - (double)distance / trail);
                    _lowerBuffer[i] = CellGrid.NotHalfBlock;
                    continue;
                }

                // The rain has passed: the new image has settled here.
                _glyphBuffer[i] = incoming.Glyph[i];
                _colourBuffer[i] = incoming.Colour[i];
                _lowerBuffer[i] = incoming.Lower[i];
            }
        }
    }

    /// <summary>Copies a settled grid straight into the buffers, for the hold phase.</summary>
    public void SetTarget(CellGrid target)
    {
        Array.Copy(target.Glyph, _glyphBuffer, _glyphBuffer.Length);
        Array.Copy(target.Colour, _colourBuffer, _colourBuffer.Length);
        Array.Copy(target.Lower, _lowerBuffer, _lowerBuffer.Length);
    }

    /// <summary>Clears both buffers to black.</summary>
    public void Clear()
    {
        Array.Clear(_glyphBuffer);
        Array.Clear(_colourBuffer);
        Array.Fill(_lowerBuffer, CellGrid.NotHalfBlock);
    }
}
